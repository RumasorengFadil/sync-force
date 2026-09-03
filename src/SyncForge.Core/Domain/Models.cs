namespace SyncForge.Core.Domain;

public enum ConnectionRole
{
    Source,
    Target
}

public enum SyncMode
{
    Incremental,
    TruncateReload
}

public enum SyncRunStatus
{
    Success,
    Failed,
    SkippedUnstable
}

public sealed class DbConnectionConfiguration
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ConnectionRole Role { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SyncJob
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long ConnectionSourceId { get; set; }
    public long ConnectionTargetId { get; set; }
    public string SourceTable { get; set; } = string.Empty;
    public string TargetTable { get; set; } = string.Empty;
    public SyncMode Mode { get; set; } = SyncMode.Incremental;
    public string? TimestampColumn { get; set; }
    public string SourcePrimaryKey { get; set; } = string.Empty;
    public string TargetPrimaryKey { get; set; } = string.Empty;
    public long MinExpectedRowCount { get; set; }
    public decimal MaxDropPercentageThreshold { get; set; } = 30;
    public bool StabilityCheckEnabled { get; set; } = true;
    public int StabilityCheckDelaySeconds { get; set; } = 15;
    public string ScheduleCron { get; set; } = "0 2 * * *";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<ColumnMapping> ColumnMappings { get; set; } = Array.Empty<ColumnMapping>();
}

public sealed class ColumnMapping
{
    public long Id { get; set; }
    public long SyncJobId { get; set; }
    public string SourceColumn { get; set; } = string.Empty;
    public string TargetColumn { get; set; } = string.Empty;
    public bool IsPrimaryKey { get; set; }
}

public sealed class SyncHistoryRecord
{
    public long Id { get; set; }
    public long SyncJobId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public SyncRunStatus Status { get; set; }
    public long RowsProcessed { get; set; }
    public long? SourceRowCount { get; set; }
    public string? ErrorMessage { get; set; }
    public string? LastCheckpointValue { get; set; }
    public string JobName { get; set; } = string.Empty;
}

public sealed record DatabaseTable(string Schema, string Name)
{
    public string QualifiedName => $"{Schema}.{Name}";
}

public sealed record DatabaseColumn(string Schema, string Table, string Name, string DataType, bool IsNullable);

public sealed record GuardRailResult(bool IsSafe, long FirstCount, long? SecondCount, string? Reason)
{
    public static GuardRailResult Safe(long firstCount, long? secondCount = null) => new(true, firstCount, secondCount, null);
    public static GuardRailResult Unsafe(long firstCount, long? secondCount, string reason) => new(false, firstCount, secondCount, reason);
}

public sealed record SyncRunResult(SyncRunStatus Status, long RowsProcessed, long? SourceRowCount, string? Checkpoint, string? Message = null);
