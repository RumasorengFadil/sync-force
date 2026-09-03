using SyncForge.Core.Domain;

namespace SyncForge.Core.Sync;

public static class GuardRailPolicy
{
    public static GuardRailResult Evaluate(SyncJob job, long firstCount, long? secondCount, long? previousSuccessfulSourceCount)
    {
        if (firstCount < job.MinExpectedRowCount)
        {
            return GuardRailResult.Unsafe(firstCount, secondCount,
                $"Jumlah baris sumber ({firstCount:N0}) di bawah minimum ({job.MinExpectedRowCount:N0}).");
        }

        if (previousSuccessfulSourceCount is > 0)
        {
            var dropPercentage = (previousSuccessfulSourceCount.Value - firstCount) * 100m / previousSuccessfulSourceCount.Value;
            if (dropPercentage > job.MaxDropPercentageThreshold)
            {
                return GuardRailResult.Unsafe(firstCount, secondCount,
                    $"Jumlah baris sumber turun {dropPercentage:N1}% dari run sukses terakhir; ambang {job.MaxDropPercentageThreshold:N1}%.");
            }
        }

        if (job.StabilityCheckEnabled && secondCount != firstCount)
        {
            return GuardRailResult.Unsafe(firstCount, secondCount,
                $"Jumlah baris sumber berubah selama quiescence check ({firstCount:N0} menjadi {secondCount:N0}).");
        }

        return GuardRailResult.Safe(firstCount, secondCount);
    }
}
