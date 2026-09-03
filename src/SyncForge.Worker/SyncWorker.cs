using System.Collections.Concurrent;
using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using SyncForge.Core.Storage;
using SyncForge.Core.Sync;

namespace SyncForge.Worker;

public sealed class SyncWorker(
    IConfigStore configStore,
    ISyncOrchestrator orchestrator,
    ILogger<SyncWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<long, DateTime> _triggeredSlots = new();
    private readonly AsyncPolicy _retryPolicy = Policy
        .Handle<Exception>(exception => exception is not OperationCanceledException)
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
            onRetry: (exception, delay, attempt, _) =>
                logger.LogWarning(exception, "Sync gagal, retry {Attempt}/3 dalam {Delay}", attempt, delay));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await configStore.InitializeAsync(stoppingToken);
        logger.LogInformation("SyncForge Worker aktif; konfigurasi akan diperiksa setiap 30 detik.");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            await TriggerDueJobsAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TriggerDueJobsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var jobs = await configStore.GetJobsAsync(enabledOnly: true, cancellationToken: cancellationToken);
        foreach (var job in jobs)
        {
            try
            {
                var cron = CronExpression.Parse(job.ScheduleCron, CronFormat.Standard);
                var slot = cron.GetNextOccurrence(now.AddMinutes(-1), TimeZoneInfo.Local, inclusive: true);
                if (slot is null || slot.Value > now || now - slot.Value > TimeSpan.FromSeconds(45))
                {
                    continue;
                }

                if (_triggeredSlots.TryGetValue(job.Id, out var triggered) && triggered == slot.Value)
                {
                    continue;
                }

                _triggeredSlots[job.Id] = slot.Value;
                await _retryPolicy.ExecuteAsync(async token =>
                {
                    await orchestrator.RunJobAsync(job.Id, token);
                }, cancellationToken);
            }
            catch (CronFormatException exception)
            {
                logger.LogError(exception, "Cron job {JobName} tidak valid: {Cron}", job.Name, job.ScheduleCron);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Job terjadwal {JobName} gagal setelah semua retry", job.Name);
            }
        }
    }
}
