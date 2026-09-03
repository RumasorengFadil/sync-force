using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using SyncForge.Core.Domain;
using SyncForge.Core.Postgres;

namespace SyncForge.Core.Sync;

public sealed class PostgresSyncEngine(IPostgresConnectionFactory connectionFactory) : IPostgresSyncEngine
{
    public async Task<SyncRunResult> SynchronizeAsync(
        SyncJob job,
        DbConnectionConfiguration source,
        DbConnectionConfiguration target,
        string? checkpoint,
        CancellationToken cancellationToken = default)
    {
        return job.Mode switch
        {
            SyncMode.Incremental => await SynchronizeIncrementalAsync(job, source, target, checkpoint, cancellationToken),
            SyncMode.TruncateReload => await SynchronizeReloadAsync(job, source, target, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(job.Mode), job.Mode, null)
        };
    }

    private async Task<SyncRunResult> SynchronizeIncrementalAsync(
        SyncJob job,
        DbConnectionConfiguration source,
        DbConnectionConfiguration target,
        string? checkpoint,
        CancellationToken cancellationToken)
    {
        var targetTable = QualifiedTableName.Parse(job.TargetTable);
        var stageName = CreateStageName(job.Id);
        await using var targetConnection = connectionFactory.Create(target);
        await targetConnection.OpenAsync(cancellationToken);
        await using var transaction = await targetConnection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync($"CREATE TEMP TABLE {QualifiedTableName.Quote(stageName)} (LIKE {targetTable.Quoted} INCLUDING DEFAULTS) ON COMMIT DROP;", targetConnection, transaction, cancellationToken);

        var copyResult = await CopySourceToStageAsync(job, source, targetConnection, transaction, QualifiedTableName.Quote(stageName), checkpoint, cancellationToken);
        if (copyResult.RowsCopied > 0)
        {
            var upsertSql = BuildUpsertSql(job, targetTable, QualifiedTableName.Quote(stageName));
            await ExecuteAsync(upsertSql, targetConnection, transaction, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new SyncRunResult(SyncRunStatus.Success, copyResult.RowsCopied, null, copyResult.LastCheckpoint ?? checkpoint);
    }

    private async Task<SyncRunResult> SynchronizeReloadAsync(
        SyncJob job,
        DbConnectionConfiguration source,
        DbConnectionConfiguration target,
        CancellationToken cancellationToken)
    {
        var targetTable = QualifiedTableName.Parse(job.TargetTable);
        var stageName = CreateStageName(job.Id);
        var backupName = CreateBackupName(job.Id);
        var stageTable = new QualifiedTableName(targetTable.Schema, stageName);
        var backupTable = new QualifiedTableName(targetTable.Schema, backupName);

        await using var targetConnection = connectionFactory.Create(target);
        await targetConnection.OpenAsync(cancellationToken);
        await EnsureNoInboundForeignKeysAsync(targetConnection, targetTable, cancellationToken);
        await using var transaction = await targetConnection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync($"CREATE TABLE {stageTable.Quoted} (LIKE {targetTable.Quoted} INCLUDING ALL);", targetConnection, transaction, cancellationToken);

        var copyResult = await CopySourceToStageAsync(job, source, targetConnection, transaction, stageTable.Quoted, null, cancellationToken);
        await ExecuteAsync($"ALTER TABLE {targetTable.Quoted} RENAME TO {QualifiedTableName.Quote(backupName)};", targetConnection, transaction, cancellationToken);
        await ExecuteAsync($"ALTER TABLE {stageTable.Quoted} RENAME TO {QualifiedTableName.Quote(targetTable.Name)};", targetConnection, transaction, cancellationToken);
        await ExecuteAsync($"DROP TABLE {backupTable.Quoted};", targetConnection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SyncRunResult(SyncRunStatus.Success, copyResult.RowsCopied, null, null);
    }

    private async Task<CopyResult> CopySourceToStageAsync(
        SyncJob job,
        DbConnectionConfiguration source,
        NpgsqlConnection targetConnection,
        NpgsqlTransaction transaction,
        string quotedStageTable,
        string? checkpoint,
        CancellationToken cancellationToken)
    {
        var mappings = job.ColumnMappings.ToArray();
        if (mappings.Length == 0)
        {
            throw new InvalidOperationException("Job tidak memiliki mapping kolom.");
        }

        var sourceTable = QualifiedTableName.Parse(job.SourceTable);
        var sourceColumns = string.Join(", ", mappings.Select(x => QualifiedTableName.Quote(x.SourceColumn)));
        var targetColumns = string.Join(", ", mappings.Select(x => QualifiedTableName.Quote(x.TargetColumn)));
        await using var sourceConnection = connectionFactory.Create(source);
        await sourceConnection.OpenAsync(cancellationToken);
        var timestampCast = job.Mode == SyncMode.Incremental
            ? await GetTimestampCastAsync(sourceConnection, sourceTable, job.TimestampColumn!, cancellationToken)
            : null;
        var sourceSql = BuildSourceSelectSql(job, sourceTable, sourceColumns, checkpoint, timestampCast, out var timestampOrdinal);
        await using var sourceCommand = new NpgsqlCommand(sourceSql, sourceConnection);
        if (!string.IsNullOrWhiteSpace(checkpoint))
        {
            sourceCommand.Parameters.AddWithValue("checkpoint", NpgsqlDbType.Text, checkpoint);
        }

        await using var reader = await sourceCommand.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, cancellationToken);
        await using var importer = await targetConnection.BeginBinaryImportAsync($"COPY {quotedStageTable} ({targetColumns}) FROM STDIN (FORMAT BINARY)", cancellationToken);
        long rowsCopied = 0;
        string? lastCheckpoint = null;
        while (await reader.ReadAsync(cancellationToken))
        {
            await importer.StartRowAsync(cancellationToken);
            for (var index = 0; index < mappings.Length; index++)
            {
                if (reader.IsDBNull(index))
                {
                    await importer.WriteNullAsync(cancellationToken);
                }
                else
                {
                    await importer.WriteAsync(reader.GetValue(index), reader.GetDataTypeName(index), cancellationToken);
                }
            }

            if (timestampOrdinal is not null && !reader.IsDBNull(timestampOrdinal.Value))
            {
                lastCheckpoint = SerializeCheckpoint(reader.GetValue(timestampOrdinal.Value));
            }

            rowsCopied++;
        }

        await importer.CompleteAsync(cancellationToken);
        return new CopyResult(rowsCopied, lastCheckpoint);
    }

    private static string BuildSourceSelectSql(
        SyncJob job,
        QualifiedTableName sourceTable,
        string sourceColumns,
        string? checkpoint,
        string? timestampCast,
        out int? timestampOrdinal)
    {
        if (job.Mode != SyncMode.Incremental)
        {
            timestampOrdinal = null;
            return $"SELECT {sourceColumns} FROM {sourceTable.Quoted};";
        }

        var timestampColumn = QualifiedTableName.Quote(job.TimestampColumn!);
        var mappedTimestampIndex = job.ColumnMappings
            .Select((mapping, index) => new { mapping, index })
            .Where(x => string.Equals(x.mapping.SourceColumn, job.TimestampColumn, StringComparison.Ordinal))
            .Select(x => (int?)x.index)
            .SingleOrDefault();
        timestampOrdinal = mappedTimestampIndex ?? job.ColumnMappings.Count;
        var selectedColumns = mappedTimestampIndex is null
            ? $"{sourceColumns}, {timestampColumn} AS \"__syncforge_checkpoint\""
            : sourceColumns;
        var predicate = string.IsNullOrWhiteSpace(checkpoint)
            ? string.Empty
            : $" WHERE {timestampColumn} > CAST($checkpoint AS {timestampCast})";
        return $"SELECT {selectedColumns} FROM {sourceTable.Quoted}{predicate} ORDER BY {timestampColumn} ASC;";
    }

    private static async Task<string> GetTimestampCastAsync(
        NpgsqlConnection connection,
        QualifiedTableName sourceTable,
        string timestampColumn,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT data_type FROM information_schema.columns
            WHERE table_schema = $schema AND table_name = $table AND column_name = $column;
            """, connection);
        command.Parameters.AddWithValue("schema", sourceTable.Schema);
        command.Parameters.AddWithValue("table", sourceTable.Name);
        command.Parameters.AddWithValue("column", timestampColumn);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return value switch
        {
            "timestamp without time zone" => "timestamp",
            "timestamp with time zone" => "timestamptz",
            _ => throw new InvalidOperationException("Kolom checkpoint tidak lagi bertipe timestamp.")
        };
    }

    private static string BuildUpsertSql(SyncJob job, QualifiedTableName targetTable, string quotedStageTable)
    {
        var mappings = job.ColumnMappings.ToArray();
        var targetColumns = mappings.Select(x => QualifiedTableName.Quote(x.TargetColumn)).ToArray();
        var primaryKeys = mappings.Where(x => x.IsPrimaryKey).Select(x => QualifiedTableName.Quote(x.TargetColumn)).ToArray();
        if (primaryKeys.Length == 0)
        {
            throw new InvalidOperationException("Mode incremental membutuhkan mapping primary key.");
        }

        var updateColumns = mappings.Where(x => !x.IsPrimaryKey).Select(x => QualifiedTableName.Quote(x.TargetColumn)).ToArray();
        var conflictAction = updateColumns.Length == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", updateColumns.Select(column => $"{column} = EXCLUDED.{column}"));
        return $"INSERT INTO {targetTable.Quoted} ({string.Join(", ", targetColumns)}) " +
               $"SELECT {string.Join(", ", targetColumns)} FROM {quotedStageTable} " +
               $"ON CONFLICT ({string.Join(", ", primaryKeys)}) {conflictAction};";
    }

    private static async Task ExecuteAsync(string sql, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureNoInboundForeignKeysAsync(NpgsqlConnection connection, QualifiedTableName targetTable, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT COUNT(*)
            FROM pg_constraint c
            JOIN pg_class target_table ON target_table.oid = c.confrelid
            JOIN pg_namespace target_schema ON target_schema.oid = target_table.relnamespace
            WHERE c.contype = 'f'
              AND target_schema.nspname = $schema
              AND target_table.relname = $table;
            """, connection);
        command.Parameters.AddWithValue("schema", targetTable.Schema);
        command.Parameters.AddWithValue("table", targetTable.Name);
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (count > 0)
        {
            throw new InvalidOperationException(
                "Truncate & Reload tidak dapat melakukan rename-swap karena tabel tujuan direferensikan foreign key. " +
                "Gunakan mode incremental atau lepaskan dependensi terlebih dahulu.");
        }
    }

    private static string CreateStageName(long jobId) => $"syncforge_stage_{jobId}_{Guid.NewGuid().ToString("N")[..12]}";

    private static string CreateBackupName(long jobId) => $"syncforge_backup_{jobId}_{Guid.NewGuid().ToString("N")[..12]}";

    private static string SerializeCheckpoint(object value) => value switch
    {
        DateTimeOffset offset => offset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? throw new InvalidOperationException("Nilai checkpoint tidak dapat diserialisasi.")
    };

    private sealed record CopyResult(long RowsCopied, string? LastCheckpoint);
}
