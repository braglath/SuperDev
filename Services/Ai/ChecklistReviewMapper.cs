using System;
using System.Collections.Generic;
using System.Linq;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public readonly record struct ReviewItemProjection(string Title, string Category, string Notes);

public static class ChecklistReviewMapper
{
    public static ChecklistReviewRequest CreateRequest(
        string? title,
        WorkType workType,
        string? taskDescription,
        string? platform,
        string? framework,
        string? templateName,
        IReadOnlyList<ChecklistItem>? items)
    {
        var reviewItems = new List<ChecklistReviewItem>();
        var source = items ?? [];
        for (var index = 0; index < source.Count; index++)
        {
            var item = source[index];
            var (description, acceptance) = SplitNotes(item.Notes);
            reviewItems.Add(new ChecklistReviewItem
            {
                Id = item.Id,
                Alias = "i" + (index + 1),
                Section = item.Category?.Trim() ?? string.Empty,
                Title = item.Title?.Trim() ?? string.Empty,
                Description = description,
                AcceptanceCriteria = acceptance,
                Priority = item.Priority.ToString(),
                ItemType = null,
                IsCompleted = item.IsCompleted
            });
        }

        return new ChecklistReviewRequest
        {
            Title = title?.Trim() ?? string.Empty,
            TaskDescription = BlankToNull(taskDescription),
            Platform = BlankToNull(platform),
            Framework = BlankToNull(framework) ?? FrameworkFromTemplate(templateName),
            WorkType = workType,
            Items = reviewItems
        };
    }

    public static string? FrameworkFromTemplate(string? templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName))
            return null;

        const string prefix = "AI · ";
        if (!templateName.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var parts = templateName[prefix.Length..].Split(" · ", StringSplitOptions.None);
        if (parts.Length < 2)
            return null;

        return BlankToNull(parts[0]);
    }

    public static (string Description, string Acceptance) SplitNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return (string.Empty, string.Empty);

        const string marker = "\n\nAcceptance: ";
        var index = notes.IndexOf(marker, StringComparison.Ordinal);
        if (index >= 0)
            return (notes[..index].Trim(), notes[(index + marker.Length)..].Trim());

        const string prefix = "Acceptance: ";
        if (notes.StartsWith(prefix, StringComparison.Ordinal))
            return (string.Empty, notes[prefix.Length..].Trim());

        return (notes.Trim(), string.Empty);
    }

    public static string ComposeNotes(string? description, string? acceptance) =>
        AiChecklistMapper.ToNotes(new GeneratedCheckItem
        {
            Description = description ?? string.Empty,
            AcceptanceCriteria = acceptance ?? string.Empty
        });

    public static bool TryPriority(string? raw, out ChecklistPriority priority)
    {
        if (!ChecklistValidator.TryNormalizePriority(raw, out var name))
        {
            priority = default;
            return false;
        }

        priority = name switch
        {
            "Critical" => ChecklistPriority.Critical,
            "Conditional" => ChecklistPriority.Conditional,
            "Review" => ChecklistPriority.Review,
            _ => ChecklistPriority.Required
        };
        return true;
    }

    public static ReviewItemProjection ProjectImprovement(ChecklistItem current, SuggestedChecklistItem? suggested)
    {
        ArgumentNullException.ThrowIfNull(current);
        var (description, acceptance) = SplitNotes(current.Notes);
        var title = current.Title ?? string.Empty;
        var category = current.Category ?? string.Empty;
        if (suggested is not null)
        {
            if (!string.IsNullOrWhiteSpace(suggested.Title))
                title = suggested.Title.Trim();
            if (!string.IsNullOrWhiteSpace(suggested.Section))
                category = suggested.Section.Trim();
            description = MergeDescription(description, suggested);
            if (!string.IsNullOrWhiteSpace(suggested.AcceptanceCriteria))
                acceptance = suggested.AcceptanceCriteria.Trim();
        }

        return new ReviewItemProjection(title, category, ComposeNotes(description, acceptance));
    }

    public static string MergeDescription(string? current, SuggestedChecklistItem? suggested)
    {
        var existing = current?.Trim() ?? string.Empty;
        if (suggested is null || string.IsNullOrWhiteSpace(suggested.Description))
            return existing;

        var note = suggested.Description.Trim();
        if (!suggested.AppendNote)
            return note;

        if (existing.Contains(note, StringComparison.OrdinalIgnoreCase))
            return existing;

        return existing.Length == 0 ? note : existing + "\n" + note;
    }

    public static string DescribeChange(ChecklistReviewFinding finding, IReadOnlyList<ChecklistItem> currents)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (currents is null || currents.Count == 0)
            return string.Empty;

        if (currents.Count == 1)
            return DescribeChange(finding, currents[0]);

        return string.Join("\n\n", currents.Select(item => DescribeChange(finding, item)));
    }

    public static string DescribeChange(ChecklistReviewFinding finding, ChecklistItem current)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(current);
        if (finding.RecommendationType == ReviewRecommendationType.PriorityAdjustment)
        {
            var suggested = TryPriority(finding.SuggestedItem?.Priority, out var priority)
                ? priority.ToString()
                : "Not supported";
            return "Change the priority of \"" + current.Title + "\"?\n\nCurrent: " + current.Priority + "\nSuggested: " + suggested;
        }

        var projected = ProjectImprovement(current, finding.SuggestedItem);
        return "Replace \"" + current.Title + "\"?\n\nCurrent\n"
            + FormatItem(current.Title, current.Category, current.Priority, current.Notes)
            + "\n\nProposed\n"
            + FormatItem(projected.Title, projected.Category, current.Priority, projected.Notes);
    }

    public static ChecklistItem ToNewItem(SuggestedChecklistItem suggested, string? category)
    {
        ArgumentNullException.ThrowIfNull(suggested);
        TryPriority(suggested.Priority, out var priority);
        if (string.IsNullOrWhiteSpace(suggested.Priority) || !TryPriority(suggested.Priority, out priority))
            priority = ChecklistPriority.Required;

        return new ChecklistItem
        {
            Id = Guid.NewGuid(),
            Title = suggested.Title.Trim(),
            Category = string.IsNullOrWhiteSpace(category) ? string.Empty : category.Trim(),
            Priority = priority,
            Notes = ComposeNotes(suggested.Description, suggested.AcceptanceCriteria),
            IsCompleted = false
        };
    }

    private static string FormatItem(string? title, string? category, ChecklistPriority priority, string? notes)
    {
        var section = string.IsNullOrWhiteSpace(category) ? "(no section)" : category.Trim();
        var body = string.IsNullOrWhiteSpace(notes) ? "No notes." : Clip(notes.Trim(), 240);
        return Clip(title?.Trim() ?? string.Empty, 120) + "\nSection: " + section + "\nPriority: " + priority + "\nNotes: " + body;
    }

    private static string Clip(string value, int max)
    {
        if (value.Length <= max)
            return value;

        return value[..max].TrimEnd() + "…";
    }

    private static string? BlankToNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }
}
