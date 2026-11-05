using System.Collections.Generic;

namespace SuperDev.Models;

public sealed class TaskExplainChild
{
    public string Title { get; init; } = string.Empty;

    public bool IsCompleted { get; init; }

    public bool Optional { get; init; }
}

/// <summary>
/// One checklist item, plus the checklist context the local model is allowed to see.
/// </summary>
public sealed class TaskExplainRequest
{
    public string ChecklistTitle { get; init; } = string.Empty;

    public string? TaskDescription { get; init; }

    public string? Platform { get; init; }

    public string? Framework { get; init; }

    public WorkType WorkType { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string Priority { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    public string? Acceptance { get; init; }

    public string? ItemType { get; init; }

    public string? TestReports { get; init; }

    public string? Logs { get; init; }

    public string? CodeReferences { get; init; }

    public string? ApiResponses { get; init; }

    public string? PullRequestLinks { get; init; }

    public bool IsCompleted { get; init; }

    public IReadOnlyList<TaskExplainChild> Subtasks { get; init; } = [];

    public IReadOnlyList<TaskExplainChild> Links { get; init; } = [];
}

public sealed class TaskExplanation
{
    public string Summary { get; init; } = string.Empty;

    public string WhyItMatters { get; init; } = string.Empty;

    public IReadOnlyList<string> Steps { get; init; } = [];

    public string DoneWhen { get; init; } = string.Empty;
}

public sealed class TaskExplainInspection
{
    public bool IsValid { get; init; }

    public TaskExplanation? Explanation { get; init; }

    public AiFailureKind? FailureKind { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class TaskExplainOutcome
{
    public TaskExplanation? Explanation { get; init; }

    public AiFailure? Failure { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool Succeeded => Failure is null && Explanation is not null;
}
