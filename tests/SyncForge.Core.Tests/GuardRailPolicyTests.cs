using SyncForge.Core.Domain;
using SyncForge.Core.Sync;

namespace SyncForge.Core.Tests;

public sealed class GuardRailPolicyTests
{
    [Fact]
    public void Evaluate_returns_unsafe_when_source_is_below_minimum()
    {
        var job = NewJob(minimumRows: 100);

        var result = GuardRailPolicy.Evaluate(job, firstCount: 99, secondCount: 99, previousSuccessfulSourceCount: 1000);

        Assert.False(result.IsSafe);
        Assert.Contains("minimum", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_returns_unsafe_when_row_count_drops_past_threshold()
    {
        var job = NewJob(maxDropPercentage: 20);

        var result = GuardRailPolicy.Evaluate(job, firstCount: 750, secondCount: 750, previousSuccessfulSourceCount: 1000);

        Assert.False(result.IsSafe);
        Assert.Contains("turun", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_returns_unsafe_when_source_changes_during_quiescence_check()
    {
        var job = NewJob();

        var result = GuardRailPolicy.Evaluate(job, firstCount: 1000, secondCount: 1001, previousSuccessfulSourceCount: 1000);

        Assert.False(result.IsSafe);
        Assert.Contains("berubah", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_allows_stable_source_inside_guard_rail_limits()
    {
        var job = NewJob(minimumRows: 100, maxDropPercentage: 30);

        var result = GuardRailPolicy.Evaluate(job, firstCount: 900, secondCount: 900, previousSuccessfulSourceCount: 1000);

        Assert.True(result.IsSafe);
        Assert.Null(result.Reason);
    }

    private static SyncJob NewJob(long minimumRows = 0, decimal maxDropPercentage = 30) => new()
    {
        MinExpectedRowCount = minimumRows,
        MaxDropPercentageThreshold = maxDropPercentage,
        StabilityCheckEnabled = true
    };
}
