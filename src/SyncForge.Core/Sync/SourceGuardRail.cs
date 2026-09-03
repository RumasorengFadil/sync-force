using Npgsql;
using SyncForge.Core.Domain;
using SyncForge.Core.Postgres;

namespace SyncForge.Core.Sync;

public interface ISourceGuardRail
{
    Task<GuardRailResult> CheckAsync(SyncJob job, DbConnectionConfiguration source, long? previousSuccessfulSourceCount, CancellationToken cancellationToken = default);
}

public sealed class SourceGuardRail(IPostgresConnectionFactory connectionFactory) : ISourceGuardRail
{
    public async Task<GuardRailResult> CheckAsync(SyncJob job, DbConnectionConfiguration source, long? previousSuccessfulSourceCount, CancellationToken cancellationToken = default)
    {
        var sourceTable = QualifiedTableName.Parse(job.SourceTable);
        await using var connection = connectionFactory.Create(source);
        await connection.OpenAsync(cancellationToken);
        var firstCount = await CountAsync(connection, sourceTable, cancellationToken);
        long? secondCount = null;
        if (job.StabilityCheckEnabled)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(job.StabilityCheckDelaySeconds, 1, 300)), cancellationToken);
            secondCount = await CountAsync(connection, sourceTable, cancellationToken);
        }

        return GuardRailPolicy.Evaluate(job, firstCount, secondCount, previousSuccessfulSourceCount);
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, QualifiedTableName table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM {table.Quoted};", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }
}
