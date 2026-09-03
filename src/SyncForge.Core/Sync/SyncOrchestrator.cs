using Microsoft.Extensions.Logging;
using SyncForge.Core.Domain;
using SyncForge.Core.Postgres;
using SyncForge.Core.Storage;

namespace SyncForge.Core.Sync;

public interface ISyncOrchestrator
{
    Task<SyncRunResult> RunJobAsync(long jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncRunResult>> RunEnabledJobsAsync(CancellationToken cancellationToken = default);
}

public sealed class SyncOrchestrator(
    IConfigStore configStore,
    IPostgresSchemaReader schemaReader,
    ISourceGuardRail guardRail,
    IPostgresSyncEngine syncEngine,
    ILogger<SyncOrchestrator> logger) : ISyncOrchestrator
{
    public async Task<SyncRunResult> RunJobAsync(long jobId, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var job = await configStore.GetJobAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job {jobId} tidak ditemukan.");
        if (!job.Enabled)
        {
            return new SyncRunResult(SyncRunStatus.SkippedUnstable, 0, null, null, "Job dinonaktifkan.");
        }

        try
        {
            var source = await configStore.GetConnectionAsync(job.ConnectionSourceId, cancellationToken)
                ?? throw new InvalidOperationException("Koneksi source tidak ditemukan.");
            var target = await configStore.GetConnectionAsync(job.ConnectionTargetId, cancellationToken)
                ?? throw new InvalidOperationException("Koneksi target tidak ditemukan.");
            if (source.Role != ConnectionRole.Source || target.Role != ConnectionRole.Target)
            {
                throw new InvalidOperationException("Peran koneksi source/target pada job tidak valid.");
            }

            await schemaReader.ValidateMappingAsync(job, source, target, cancellationToken);
            var latestSuccess = await configStore.GetLatestSuccessfulHistoryAsync(job.Id, cancellationToken);
            var guard = await guardRail.CheckAsync(job, source, latestSuccess?.SourceRowCount, cancellationToken);
            if (!guard.IsSafe)
            {
                logger.LogWarning("Job {JobName} dilewati oleh guard rail: {Reason}", job.Name, guard.Reason);
                await configStore.AddHistoryAsync(new SyncHistoryRecord
                {
                    SyncJobId = job.Id,
                    StartedAt = startedAt,
                    FinishedAt = DateTimeOffset.UtcNow,
                    Status = SyncRunStatus.SkippedUnstable,
                    SourceRowCount = guard.FirstCount,
                    ErrorMessage = guard.Reason
                }, cancellationToken);
                return new SyncRunResult(SyncRunStatus.SkippedUnstable, 0, guard.FirstCount, null, guard.Reason);
            }

            logger.LogInformation("Memulai sync job {JobName} ({Mode})", job.Name, job.Mode);
            var result = await syncEngine.SynchronizeAsync(job, source, target, latestSuccess?.LastCheckpointValue, cancellationToken);
            await configStore.AddHistoryAsync(new SyncHistoryRecord
            {
                SyncJobId = job.Id,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.UtcNow,
                Status = SyncRunStatus.Success,
                RowsProcessed = result.RowsProcessed,
                SourceRowCount = guard.FirstCount,
                LastCheckpointValue = result.Checkpoint
            }, cancellationToken);
            logger.LogInformation("Job {JobName} selesai: {RowsProcessed} baris", job.Name, result.RowsProcessed);
            return result with { SourceRowCount = guard.FirstCount };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Job {JobName} gagal", job.Name);
            await configStore.AddHistoryAsync(new SyncHistoryRecord
            {
                SyncJobId = job.Id,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.UtcNow,
                Status = SyncRunStatus.Failed,
                ErrorMessage = exception.Message
            }, CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<SyncRunResult>> RunEnabledJobsAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<SyncRunResult>();
        var jobs = await configStore.GetJobsAsync(enabledOnly: true, cancellationToken: cancellationToken);
        foreach (var job in jobs)
        {
            results.Add(await RunJobAsync(job.Id, cancellationToken));
        }

        return results;
    }
}
