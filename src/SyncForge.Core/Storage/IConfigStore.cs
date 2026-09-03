using SyncForge.Core.Domain;

namespace SyncForge.Core.Storage;

public interface IConfigStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DbConnectionConfiguration>> GetConnectionsAsync(CancellationToken cancellationToken = default);
    Task<DbConnectionConfiguration?> GetConnectionAsync(long id, CancellationToken cancellationToken = default);
    Task<long> SaveConnectionAsync(DbConnectionConfiguration configuration, CancellationToken cancellationToken = default);
    Task DeleteConnectionAsync(long id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncJob>> GetJobsAsync(bool enabledOnly = false, CancellationToken cancellationToken = default);
    Task<SyncJob?> GetJobAsync(long id, CancellationToken cancellationToken = default);
    Task<long> SaveJobAsync(SyncJob job, IReadOnlyCollection<ColumnMapping> mappings, CancellationToken cancellationToken = default);
    Task DeleteJobAsync(long id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncHistoryRecord>> GetHistoryAsync(int take = 200, CancellationToken cancellationToken = default);
    Task<SyncHistoryRecord?> GetLatestSuccessfulHistoryAsync(long syncJobId, CancellationToken cancellationToken = default);
    Task AddHistoryAsync(SyncHistoryRecord record, CancellationToken cancellationToken = default);
}
