using SyncForge.Core.Domain;

namespace SyncForge.Core.Sync;

public interface IPostgresSyncEngine
{
    Task<SyncRunResult> SynchronizeAsync(
        SyncJob job,
        DbConnectionConfiguration source,
        DbConnectionConfiguration target,
        string? checkpoint,
        CancellationToken cancellationToken = default);
}
