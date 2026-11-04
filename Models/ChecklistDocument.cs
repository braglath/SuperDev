using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SuperDev.Models;

public sealed class ChecklistDocument
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string BranchName { get; set; } = string.Empty;

    public string PullRequestUrl { get; set; } = string.Empty;

    public WorkType WorkType { get; set; }

    /// <summary>
    /// Name of the home template this checklist was created from. Missing on older files.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TemplateName { get; set; }

    /// <summary>
    /// Optional calendar deadline. Missing on older files, and left unset until the user picks one.
    /// </summary>
    public DateOnly? Deadline { get; set; }

    /// <summary>
    /// Local minutes from midnight when a time was picked. Missing on older date-only deadlines.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DeadlineMinute { get; set; }

    /// <summary>
    /// Null skips a deadline notification. Zero notifies at the deadline. A positive value is minutes before it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NotifyBeforeMinutes { get; set; }

    /// <summary>
    /// When set, the deadline notification is delivered without a sound.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DeadlineSilent { get; set; }

    /// <summary>
    /// Next reminder for the whole checklist, stored in UTC. Missing until the user sets one.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ReminderOn { get; set; }

    /// <summary>
    /// Minutes between repeats. Zero is a one-time reminder.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ReminderEveryMinutes { get; set; }

    /// <summary>
    /// When set, this checklist reminder is delivered without a sound.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReminderSilent { get; set; }

    /// <summary>
    /// UTC timestamp from when the document was first created. Saves do not change it.
    /// </summary>
    public DateTime CreatedOn { get; set; }

    /// <summary>
    /// UTC timestamp of the last successful save. Opening a file does not change it.
    /// </summary>
    public DateTime UpdatedOn { get; set; }

    /// <summary>
    /// Set only by the Complete action. Item checkboxes do not set this.
    /// </summary>
    public bool IsCompleted { get; set; }

    public List<ChecklistItem> Items { get; set; } = [];
}
