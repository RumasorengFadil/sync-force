using Npgsql;
using SyncForge.Core.Domain;

namespace SyncForge.Core.Postgres;

public interface IPostgresSchemaReader
{
    Task<IReadOnlyList<DatabaseTable>> GetTablesAsync(DbConnectionConfiguration configuration, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DatabaseColumn>> GetColumnsAsync(DbConnectionConfiguration configuration, string configuredTable, CancellationToken cancellationToken = default);
    Task ValidateMappingAsync(SyncJob job, DbConnectionConfiguration source, DbConnectionConfiguration target, CancellationToken cancellationToken = default);
}

public sealed class PostgresSchemaReader(IPostgresConnectionFactory connectionFactory) : IPostgresSchemaReader
{
    public async Task<IReadOnlyList<DatabaseTable>> GetTablesAsync(DbConnectionConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var items = new List<DatabaseTable>();
        await using var connection = connectionFactory.Create(configuration);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT table_schema, table_name
            FROM information_schema.tables
            WHERE table_type = 'BASE TABLE'
                AND table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY table_schema, table_name;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new DatabaseTable(reader.GetString(0), reader.GetString(1)));
        }

        return items;
    }

    public async Task<IReadOnlyList<DatabaseColumn>> GetColumnsAsync(DbConnectionConfiguration configuration, string configuredTable, CancellationToken cancellationToken = default)
    {
        var table = QualifiedTableName.Parse(configuredTable);
        var items = new List<DatabaseColumn>();
        await using var connection = connectionFactory.Create(configuration);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT table_schema, table_name, column_name, data_type, is_nullable
            FROM information_schema.columns
            WHERE table_schema = $schema AND table_name = $table
            ORDER BY ordinal_position;
            """, connection);
        command.Parameters.AddWithValue("schema", table.Schema);
        command.Parameters.AddWithValue("table", table.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new DatabaseColumn(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4) == "YES"));
        }

        return items;
    }

    public async Task ValidateMappingAsync(SyncJob job, DbConnectionConfiguration source, DbConnectionConfiguration target, CancellationToken cancellationToken = default)
    {
        var sourceColumns = await GetColumnsAsync(source, job.SourceTable, cancellationToken);
        var targetColumns = await GetColumnsAsync(target, job.TargetTable, cancellationToken);
        if (sourceColumns.Count == 0 || targetColumns.Count == 0)
        {
            throw new InvalidOperationException("Tabel sumber atau tujuan tidak ditemukan. Perbarui mapping sebelum menjalankan job.");
        }

        var sourceSet = sourceColumns.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var targetSet = targetColumns.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var missingSource = job.ColumnMappings.Where(x => !sourceSet.Contains(x.SourceColumn)).Select(x => x.SourceColumn).Distinct().ToArray();
        var missingTarget = job.ColumnMappings.Where(x => !targetSet.Contains(x.TargetColumn)).Select(x => x.TargetColumn).Distinct().ToArray();
        if (missingSource.Length > 0 || missingTarget.Length > 0)
        {
            var details = new List<string>();
            if (missingSource.Length > 0) details.Add($"kolom sumber hilang: {string.Join(", ", missingSource)}");
            if (missingTarget.Length > 0) details.Add($"kolom tujuan hilang: {string.Join(", ", missingTarget)}");
            throw new InvalidOperationException($"Mapping tidak lagi valid - {string.Join("; ", details)}.");
        }

        var sourceByName = sourceColumns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var targetByName = targetColumns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var incompatibleMappings = job.ColumnMappings
            .Where(mapping => !string.Equals(sourceByName[mapping.SourceColumn].DataType, targetByName[mapping.TargetColumn].DataType, StringComparison.OrdinalIgnoreCase))
            .Select(mapping => $"{mapping.SourceColumn} ({sourceByName[mapping.SourceColumn].DataType}) -> {mapping.TargetColumn} ({targetByName[mapping.TargetColumn].DataType})")
            .ToArray();
        if (incompatibleMappings.Length > 0)
        {
            throw new InvalidOperationException($"Tipe kolom source/target tidak kompatibel untuk binary COPY: {string.Join("; ", incompatibleMappings)}.");
        }

        if (job.Mode == SyncMode.Incremental)
        {
            var timestamp = sourceColumns.SingleOrDefault(x => string.Equals(x.Name, job.TimestampColumn, StringComparison.Ordinal));
            if (timestamp is null || !timestamp.DataType.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Kolom checkpoint harus berupa timestamp pada tabel sumber.");
            }
        }
    }
}
