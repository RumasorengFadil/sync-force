using Npgsql;
using SyncForge.Core.Domain;

namespace SyncForge.Core.Postgres;

public interface IPostgresConnectionFactory
{
    NpgsqlConnection Create(DbConnectionConfiguration configuration);
    Task TestConnectionAsync(DbConnectionConfiguration configuration, CancellationToken cancellationToken = default);
}

public sealed class PostgresConnectionFactory : IPostgresConnectionFactory
{
    public NpgsqlConnection Create(DbConnectionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = configuration.Host,
            Port = configuration.Port,
            Database = configuration.Database,
            Username = configuration.Username,
            Password = configuration.Password,
            Timeout = 10,
            CommandTimeout = 0,
            ApplicationName = "SyncForge",
            Pooling = true,
            KeepAlive = 30
        };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    public async Task TestConnectionAsync(DbConnectionConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await using var connection = Create(configuration);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1;", connection);
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
