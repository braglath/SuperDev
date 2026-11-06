using System;
using System.Text.Json.Serialization;

namespace SuperDev.Models;

/// <summary>
/// Index entry for a checklist file so a project can list its tasklists
/// without scanning the disk. The checklist file remains the document.
/// </summary>
public sealed class TasklistRecord
{
    public Guid DocumentId { get; set; }

    public string Path { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public Guid? ProjectId { get; set; }

    /// <summary>Position among the tasklists of <see cref="ProjectId"/>. Lower comes first.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Order { get; set; }

    public bool IsCompleted { get; set; }

    public int CompletedChecks { get; set; }

    public int TotalChecks { get; set; }

    public int SkippedChecks { get; set; }

    public DateTime UpdatedOn { get; set; }
}
