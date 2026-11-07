using System;

namespace SuperDev.Models;

/// <summary>
/// A real checklist transition. Saving the same state again does not add another event.
/// </summary>
public enum ActivityEventType
{
    TasklistCreated,
    TasklistStarted,
    TasklistCompleted,
    TasklistReopened,
    TasklistDeleted,
    CheckCompleted,
    CheckReopened
}

/// <summary>
/// Current tasklist lifecycle. Pending has never had a completed check.
/// In progress has completed work and is not marked complete. Completed was explicitly finished.
/// </summary>
public enum TasklistActivityStatus
{
    Pending,
    InProgress,
    Completed
}

public sealed class ActivityEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid TasklistId { get; init; }

    public Guid? ProjectId { get; init; }

    public Guid? ChecklistItemId { get; init; }

    public ActivityEventType EventType { get; init; }

    /// <summary>UTC timestamp of the transition.</summary>
    public DateTime EventTimestamp { get; init; }

    /// <summary>Optional JSON context. Check events usually leave this empty.</summary>
    public string? Metadata { get; init; }
}
