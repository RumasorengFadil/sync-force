using Microsoft.Data.Sqlite;
using SyncForge.Core.Configuration;
using SyncForge.Core.Domain;

namespace SyncForge.Core.Storage;

public sealed class SqliteConfigStore(string databasePath, ICredentialProtector credentialProtector) : IConfigStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public static SqliteConfigStore CreateDefault(ICredentialProtector credentialProtector)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        return new SqliteConfigStore(AppPaths.DatabasePath, credentialProtector);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS connections (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                role TEXT NOT NULL CHECK (role IN ('source', 'target')),
                host TEXT NOT NULL,
                port INTEGER NOT NULL,
                database_name TEXT NOT NULL,
                username TEXT NOT NULL,
                password_encrypted TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sync_jobs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                connection_source_id INTEGER NOT NULL REFERENCES connections(id) ON DELETE RESTRICT,
                connection_target_id INTEGER NOT NULL REFERENCES connections(id) ON DELETE RESTRICT,
                source_table TEXT NOT NULL,
                target_table TEXT NOT NULL,
                mode TEXT NOT NULL CHECK (mode IN ('incremental', 'truncate_reload')),
                timestamp_column TEXT NULL,
                source_pk TEXT NOT NULL,
                target_pk TEXT NOT NULL,
                min_expected_row_count INTEGER NOT NULL DEFAULT 0,
                max_drop_percentage_threshold REAL NOT NULL DEFAULT 30,
                stability_check_enabled INTEGER NOT NULL DEFAULT 1,
                stability_check_delay_seconds INTEGER NOT NULL DEFAULT 15,
                schedule_cron TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS column_mappings (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sync_job_id INTEGER NOT NULL REFERENCES sync_jobs(id) ON DELETE CASCADE,
                source_column TEXT NOT NULL,
                target_column TEXT NOT NULL,
                is_primary_key INTEGER NOT NULL DEFAULT 0,
                UNIQUE(sync_job_id, source_column),
                UNIQUE(sync_job_id, target_column)
            );

            CREATE TABLE IF NOT EXISTS sync_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sync_job_id INTEGER NOT NULL REFERENCES sync_jobs(id) ON DELETE CASCADE,
                started_at TEXT NOT NULL,
                finished_at TEXT NULL,
                status TEXT NOT NULL CHECK (status IN ('success', 'failed', 'skipped_unstable')),
                rows_processed INTEGER NOT NULL DEFAULT 0,
                source_row_count INTEGER NULL,
                error_message TEXT NULL,
                last_checkpoint_value TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_history_job_status_finished
                ON sync_history(sync_job_id, status, finished_at DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DbConnectionConfiguration>> GetConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<DbConnectionConfiguration>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, role, host, port, database_name, username, password_encrypted, created_at FROM connections ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadConnection(reader));
        }

        return items;
    }

    public async Task<DbConnectionConfiguration?> GetConnectionAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, role, host, port, database_name, username, password_encrypted, created_at FROM connections WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadConnection(reader) : null;
    }

    public async Task<long> SaveConnectionAsync(DbConnectionConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ValidateConnection(configuration);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = configuration.Id == 0
            ? """
                INSERT INTO connections (name, role, host, port, database_name, username, password_encrypted, created_at)
                VALUES ($name, $role, $host, $port, $database, $username, $password, $createdAt);
                SELECT last_insert_rowid();
                """
            : """
                UPDATE connections SET name = $name, role = $role, host = $host, port = $port,
                    database_name = $database, username = $username, password_encrypted = $password
                WHERE id = $id;
                SELECT $id;
                """;
        AddConnectionParameters(command, configuration);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task DeleteConnectionAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM connections WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SyncJob>> GetJobsAsync(bool enabledOnly = false, CancellationToken cancellationToken = default)
    {
        var jobs = new List<SyncJob>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {JobColumns} FROM sync_jobs" + (enabledOnly ? " WHERE enabled = 1" : string.Empty) + " ORDER BY name;";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                jobs.Add(ReadJob(reader));
            }
        }

        foreach (var job in jobs)
        {
            job.ColumnMappings = await GetColumnMappingsAsync(connection, job.Id, cancellationToken);
        }

        return jobs;
    }

    public async Task<SyncJob?> GetJobAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {JobColumns} FROM sync_jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var job = ReadJob(reader);
        await reader.CloseAsync();
        job.ColumnMappings = await GetColumnMappingsAsync(connection, job.Id, cancellationToken);
        return job;
    }

    public async Task<long> SaveJobAsync(SyncJob job, IReadOnlyCollection<ColumnMapping> mappings, CancellationToken cancellationToken = default)
    {
        ValidateJob(job, mappings);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = job.Id == 0
            ? """
                INSERT INTO sync_jobs (name, connection_source_id, connection_target_id, source_table, target_table, mode,
                    timestamp_column, source_pk, target_pk, min_expected_row_count, max_drop_percentage_threshold,
                    stability_check_enabled, stability_check_delay_seconds, schedule_cron, enabled, created_at)
                VALUES ($name, $sourceConnection, $targetConnection, $sourceTable, $targetTable, $mode,
                    $timestampColumn, $sourcePk, $targetPk, $minimumRows, $maximumDrop, $stabilityEnabled,
                    $stabilityDelay, $cron, $enabled, $createdAt);
                SELECT last_insert_rowid();
                """
            : """
                UPDATE sync_jobs SET name = $name, connection_source_id = $sourceConnection,
                    connection_target_id = $targetConnection, source_table = $sourceTable, target_table = $targetTable,
                    mode = $mode, timestamp_column = $timestampColumn, source_pk = $sourcePk, target_pk = $targetPk,
                    min_expected_row_count = $minimumRows, max_drop_percentage_threshold = $maximumDrop,
                    stability_check_enabled = $stabilityEnabled, stability_check_delay_seconds = $stabilityDelay,
                    schedule_cron = $cron, enabled = $enabled WHERE id = $id;
                SELECT $id;
                """;
        AddJobParameters(command, job);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);

        command.Parameters.Clear();
        command.CommandText = "DELETE FROM column_mappings WHERE sync_job_id = $jobId;";
        command.Parameters.AddWithValue("$jobId", id);
        await command.ExecuteNonQueryAsync(cancellationToken);

        foreach (var mapping in mappings)
        {
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO column_mappings (sync_job_id, source_column, target_column, is_primary_key)
                VALUES ($jobId, $source, $target, $isPrimaryKey);
                """;
            command.Parameters.AddWithValue("$jobId", id);
            command.Parameters.AddWithValue("$source", mapping.SourceColumn);
            command.Parameters.AddWithValue("$target", mapping.TargetColumn);
            command.Parameters.AddWithValue("$isPrimaryKey", mapping.IsPrimaryKey ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task DeleteJobAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sync_jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SyncHistoryRecord>> GetHistoryAsync(int take = 200, CancellationToken cancellationToken = default)
    {
        var history = new List<SyncHistoryRecord>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.id, h.sync_job_id, h.started_at, h.finished_at, h.status, h.rows_processed,
                h.source_row_count, h.error_message, h.last_checkpoint_value, j.name
            FROM sync_history h JOIN sync_jobs j ON j.id = h.sync_job_id
            ORDER BY h.started_at DESC LIMIT $take;
            """;
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            history.Add(ReadHistory(reader));
        }

        return history;
    }

    public async Task<SyncHistoryRecord?> GetLatestSuccessfulHistoryAsync(long syncJobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.id, h.sync_job_id, h.started_at, h.finished_at, h.status, h.rows_processed,
                h.source_row_count, h.error_message, h.last_checkpoint_value, j.name
            FROM sync_history h JOIN sync_jobs j ON j.id = h.sync_job_id
            WHERE h.sync_job_id = $jobId AND h.status = 'success'
            ORDER BY h.finished_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$jobId", syncJobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadHistory(reader) : null;
    }

    public async Task AddHistoryAsync(SyncHistoryRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_history (sync_job_id, started_at, finished_at, status, rows_processed,
                source_row_count, error_message, last_checkpoint_value)
            VALUES ($jobId, $startedAt, $finishedAt, $status, $rowsProcessed, $sourceRowCount, $errorMessage, $checkpoint);
            """;
        command.Parameters.AddWithValue("$jobId", record.SyncJobId);
        command.Parameters.AddWithValue("$startedAt", record.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$finishedAt", record.FinishedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", ToStorageValue(record.Status));
        command.Parameters.AddWithValue("$rowsProcessed", record.RowsProcessed);
        command.Parameters.AddWithValue("$sourceRowCount", record.SourceRowCount ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", record.ErrorMessage ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$checkpoint", record.LastCheckpointValue ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private DbConnectionConfiguration ReadConnection(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        Role = reader.GetString(2) == "source" ? ConnectionRole.Source : ConnectionRole.Target,
        Host = reader.GetString(3),
        Port = reader.GetInt32(4),
        Database = reader.GetString(5),
        Username = reader.GetString(6),
        Password = credentialProtector.Unprotect(reader.GetString(7)),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture)
    };

    private static SyncJob ReadJob(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0), Name = reader.GetString(1), ConnectionSourceId = reader.GetInt64(2),
        ConnectionTargetId = reader.GetInt64(3), SourceTable = reader.GetString(4), TargetTable = reader.GetString(5),
        Mode = reader.GetString(6) == "incremental" ? SyncMode.Incremental : SyncMode.TruncateReload,
        TimestampColumn = reader.IsDBNull(7) ? null : reader.GetString(7), SourcePrimaryKey = reader.GetString(8),
        TargetPrimaryKey = reader.GetString(9), MinExpectedRowCount = reader.GetInt64(10),
        MaxDropPercentageThreshold = Convert.ToDecimal(reader.GetValue(11), System.Globalization.CultureInfo.InvariantCulture), StabilityCheckEnabled = reader.GetInt64(12) == 1,
        StabilityCheckDelaySeconds = reader.GetInt32(13), ScheduleCron = reader.GetString(14), Enabled = reader.GetInt64(15) == 1,
        CreatedAt = DateTimeOffset.Parse(reader.GetString(16), System.Globalization.CultureInfo.InvariantCulture)
    };

    private static SyncHistoryRecord ReadHistory(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0), SyncJobId = reader.GetInt64(1),
        StartedAt = DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
        FinishedAt = reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
        Status = ParseStatus(reader.GetString(4)), RowsProcessed = reader.GetInt64(5),
        SourceRowCount = reader.IsDBNull(6) ? null : reader.GetInt64(6), ErrorMessage = reader.IsDBNull(7) ? null : reader.GetString(7),
        LastCheckpointValue = reader.IsDBNull(8) ? null : reader.GetString(8), JobName = reader.GetString(9)
    };

    private static async Task<IReadOnlyList<ColumnMapping>> GetColumnMappingsAsync(SqliteConnection connection, long jobId, CancellationToken cancellationToken)
    {
        var mappings = new List<ColumnMapping>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, sync_job_id, source_column, target_column, is_primary_key FROM column_mappings WHERE sync_job_id = $jobId ORDER BY id;";
        command.Parameters.AddWithValue("$jobId", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            mappings.Add(new ColumnMapping
            {
                Id = reader.GetInt64(0), SyncJobId = reader.GetInt64(1), SourceColumn = reader.GetString(2),
                TargetColumn = reader.GetString(3), IsPrimaryKey = reader.GetInt64(4) == 1
            });
        }

        return mappings;
    }

    private void AddConnectionParameters(SqliteCommand command, DbConnectionConfiguration configuration)
    {
        command.Parameters.AddWithValue("$id", configuration.Id);
        command.Parameters.AddWithValue("$name", configuration.Name.Trim());
        command.Parameters.AddWithValue("$role", configuration.Role == ConnectionRole.Source ? "source" : "target");
        command.Parameters.AddWithValue("$host", configuration.Host.Trim());
        command.Parameters.AddWithValue("$port", configuration.Port);
        command.Parameters.AddWithValue("$database", configuration.Database.Trim());
        command.Parameters.AddWithValue("$username", configuration.Username.Trim());
        command.Parameters.AddWithValue("$password", credentialProtector.Protect(configuration.Password));
        command.Parameters.AddWithValue("$createdAt", configuration.CreatedAt.ToString("O"));
    }

    private static void AddJobParameters(SqliteCommand command, SyncJob job)
    {
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$name", job.Name.Trim());
        command.Parameters.AddWithValue("$sourceConnection", job.ConnectionSourceId);
        command.Parameters.AddWithValue("$targetConnection", job.ConnectionTargetId);
        command.Parameters.AddWithValue("$sourceTable", job.SourceTable.Trim());
        command.Parameters.AddWithValue("$targetTable", job.TargetTable.Trim());
        command.Parameters.AddWithValue("$mode", job.Mode == SyncMode.Incremental ? "incremental" : "truncate_reload");
        command.Parameters.AddWithValue("$timestampColumn", job.TimestampColumn?.Trim() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$sourcePk", job.SourcePrimaryKey.Trim());
        command.Parameters.AddWithValue("$targetPk", job.TargetPrimaryKey.Trim());
        command.Parameters.AddWithValue("$minimumRows", job.MinExpectedRowCount);
        command.Parameters.AddWithValue("$maximumDrop", job.MaxDropPercentageThreshold);
        command.Parameters.AddWithValue("$stabilityEnabled", job.StabilityCheckEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$stabilityDelay", job.StabilityCheckDelaySeconds);
        command.Parameters.AddWithValue("$cron", job.ScheduleCron.Trim());
        command.Parameters.AddWithValue("$enabled", job.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", job.CreatedAt.ToString("O"));
    }

    private static void ValidateConnection(DbConnectionConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.Name) || string.IsNullOrWhiteSpace(configuration.Host) ||
            string.IsNullOrWhiteSpace(configuration.Database) || string.IsNullOrWhiteSpace(configuration.Username) ||
            string.IsNullOrWhiteSpace(configuration.Password) || configuration.Port is < 1 or > 65535)
        {
            throw new ArgumentException("Connection membutuhkan nama, host, port, database, username, dan password.");
        }
    }

    private static void ValidateJob(SyncJob job, IReadOnlyCollection<ColumnMapping> mappings)
    {
        if (string.IsNullOrWhiteSpace(job.Name) || job.ConnectionSourceId <= 0 || job.ConnectionTargetId <= 0 ||
            string.IsNullOrWhiteSpace(job.SourceTable) || string.IsNullOrWhiteSpace(job.TargetTable) || mappings.Count == 0)
        {
            throw new ArgumentException("Job membutuhkan nama, koneksi, tabel, dan minimal satu mapping kolom.");
        }

        if (job.Mode == SyncMode.Incremental && string.IsNullOrWhiteSpace(job.TimestampColumn))
        {
            throw new ArgumentException("Mode incremental membutuhkan kolom timestamp.");
        }

        if (!mappings.Any(x => x.IsPrimaryKey))
        {
            throw new ArgumentException("Minimal satu mapping harus ditandai sebagai primary key.");
        }
    }

    private static SyncRunStatus ParseStatus(string value) => value switch
    {
        "success" => SyncRunStatus.Success,
        "failed" => SyncRunStatus.Failed,
        "skipped_unstable" => SyncRunStatus.SkippedUnstable,
        _ => throw new InvalidOperationException($"Unknown sync status '{value}'.")
    };

    private static string ToStorageValue(SyncRunStatus status) => status switch
    {
        SyncRunStatus.Success => "success",
        SyncRunStatus.Failed => "failed",
        SyncRunStatus.SkippedUnstable => "skipped_unstable",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    private const string JobColumns = "id, name, connection_source_id, connection_target_id, source_table, target_table, mode, timestamp_column, source_pk, target_pk, min_expected_row_count, max_drop_percentage_threshold, stability_check_enabled, stability_check_delay_seconds, schedule_cron, enabled, created_at";
}
