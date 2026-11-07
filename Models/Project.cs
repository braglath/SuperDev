using System;
using System.Text.Json.Serialization;

namespace SuperDev.Models;

public enum ProjectStatus
{
    Active,
    Archived
}

/// <summary>
/// A project groups existing checklists. It does not own a separate task list.
/// </summary>
public sealed class Project
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public string Framework { get; set; } = string.Empty;

    /// <summary>One of <see cref="ProjectAppearance.Icons"/>.</summary>
    public string Icon { get; set; } = ProjectAppearance.DefaultIcon;

    /// <summary>A #RRGGBB value from <see cref="ProjectAppearance.Colors"/>.</summary>
    public string Color { get; set; } = ProjectAppearance.DefaultColor;

    public ProjectStatus Status { get; set; } = ProjectStatus.Active;

    /// <summary>Optional calendar deadline. Missing until the user picks one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? Deadline { get; set; }

    /// <summary>Local minutes from midnight when a time was picked.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DeadlineMinute { get; set; }

    /// <summary>Null skips a deadline notification. Zero notifies at the deadline. A positive value is minutes before it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NotifyBeforeMinutes { get; set; }

    /// <summary>When set, the deadline notification is delivered without a sound.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DeadlineSilent { get; set; }

    /// <summary>Next reminder for the project, stored in UTC. Missing until the user sets one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ReminderOn { get; set; }

    /// <summary>Minutes between repeats. Zero is a one-time reminder.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ReminderEveryMinutes { get; set; }

    /// <summary>When set, this project reminder is delivered without a sound.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReminderSilent { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public sealed record ProjectDraft(
    string Name,
    string Description,
    string Platform,
    string Framework,
    string Icon,
    string Color);

public static class ProjectAppearance
{
    public const string DefaultIcon = "Folder";

    public const string DefaultColor = "#8B5CF6";

    public static readonly string[] Icons =
    [
        "Folder",
        "Code",
        "Laptop",
        "Layers",
        "Rocket",
        "Box",
        "Star"
    ];

    /// <summary>Icons saved before the picker matched the create-project design.</summary>
    public static readonly string[] LegacyIcons =
    [
        "Bug",
        "Shield",
        "Book",
        "Spark"
    ];

    public static readonly string[] Colors =
    [
        "#8B5CF6",
        "#3B82F6",
        "#14B8A6",
        "#22C55E",
        "#F59E0B",
        "#EF4444",
        "#EC4899",
        "#64748B"
    ];

    public static string Icon(string? value) =>
        Array.Exists(Icons, icon => string.Equals(icon, value, StringComparison.Ordinal))
        || Array.Exists(LegacyIcons, icon => string.Equals(icon, value, StringComparison.Ordinal))
            ? value!
            : DefaultIcon;

    public static string Color(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DefaultColor;

        foreach (var color in Colors)
        {
            if (string.Equals(color, value.Trim(), StringComparison.OrdinalIgnoreCase))
                return color;
        }

        return DefaultColor;
    }
}
