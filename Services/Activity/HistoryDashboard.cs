using System;
using System.Collections.Generic;
using SuperDev.Models;

namespace SuperDev.Services.Activity;

public sealed class TrendBucket
{
    public DateOnly Start { get; init; }

    public DateOnly End { get; init; }

    public int Count { get; init; }

    public string Label { get; init; } = string.Empty;
}

public sealed class CompletedTasklistEntry
{
    public Guid TasklistId { get; init; }

    public Guid? ProjectId { get; init; }

    public string? ProjectName { get; init; }

    public string Title { get; init; } = string.Empty;

    public WorkType WorkType { get; init; }

    public string? Framework { get; init; }

    public string? FilePath { get; init; }

    public DateTime CompletedAtUtc { get; init; }
}

public sealed class HistoryDashboard
{
    public DateRangeFilter Range { get; init; } = null!;

    public int CompletedTasklists { get; init; }

    public int StartedTasklists { get; init; }

    public int InProgressTasklists { get; init; }

    public int PendingTasklists { get; init; }

    public int ChecksCompleted { get; init; }

    public int ChecksReopened { get; init; }

    public int PendingChecks { get; init; }

    public int CompletionRatePercent { get; init; }

    public TrendGranularity Granularity { get; init; }

    public IReadOnlyList<TrendBucket> Trend { get; init; } = [];

    public IReadOnlyList<CompletedTasklistEntry> Completed { get; init; } = [];

    public int CompletedMatchCount { get; init; }
}

public sealed class HistoryQuery
{
    public required DateRangeFilter Range { get; init; }

    public required TimeZoneInfo Zone { get; init; }

    public HistoryProjectScope ProjectScope { get; init; } = HistoryProjectScope.All;

    public Guid? ProjectId { get; init; }

    public string? TitleSearch { get; init; }

    public WorkType? WorkType { get; init; }

    public TrendGranularity? Granularity { get; init; }

    public int CompletedLimit { get; init; } = 4;

    public IReadOnlyDictionary<Guid, string>? ProjectNames { get; init; }
}
