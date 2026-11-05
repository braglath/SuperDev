using System;
using System.Collections.Generic;
using System.Linq;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public readonly record struct ReviewItemProjection(
    string Title,
    string Category,
    string Notes,
    string Acceptance,
    string ItemType);

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
            var (description, acceptance) = ReadDetails(item);
            reviewItems.Add(new ChecklistReviewItem
            {
                Id = item.Id,
                Alias = "i" + (index + 1),
                Section = item.Category?.Trim() ?? string.Empty,
                Title = item.Title?.Trim() ?? string.Empty,
                Description = description,
                AcceptanceCriteria = acceptance,
                Priority = item.Priority.ToString(),
                ItemType = item.ItemType,
                TestReports = item.TestReports ?? string.Empty,
                Logs = item.Logs ?? string.Empty,
                CodeReferences = item.CodeReferences ?? string.Empty,
                ApiResponses = item.ApiResponses ?? string.Empty,
                PullRequestLinks = item.PullRequestLinks ?? string.Empty,
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

    /// <summary>
    /// Moves an older "Acceptance:" note into <see cref="ChecklistItem.Acceptance"/>.
    /// A check that already has acceptance is left as it was saved.
    /// </summary>
    public static void SeparateLegacyAcceptance(ChecklistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.HasAcceptance)
            return;

        var (description, acceptance) = SplitNotes(item.Notes);
        if (acceptance.Length == 0)
            return;

        item.Notes = description;
        item.Acceptance = acceptance;
    }

    public static (string Description, string Acceptance) ReadDetails(ChecklistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var (description, embedded) = SplitNotes(item.Notes);
        var acceptance = item.HasAcceptance ? item.Acceptance!.Trim() : embedded;
        return (description, acceptance);
    }

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
        var (description, acceptance) = ReadDetails(current);
        var title = current.Title ?? string.Empty;
        var category = current.Category ?? string.Empty;
        var itemType = current.ItemType ?? string.Empty;
        if (suggested is not null)
        {
            if (!string.IsNullOrWhiteSpace(suggested.Title))
                title = suggested.Title.Trim();
            if (!string.IsNullOrWhiteSpace(suggested.Section))
                category = suggested.Section.Trim();
            description = MergeDescription(description, suggested);
            if (!string.IsNullOrWhiteSpace(suggested.AcceptanceCriteria))
                acceptance = suggested.AcceptanceCriteria.Trim();
            if (ChecklistVocabulary.CanonicalItemType(suggested.Type) is { } type)
                itemType = type;
        }

        return new ReviewItemProjection(title, category, description, acceptance, itemType);
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

        var currentView = ProjectImprovement(current, null);
        var projected = ProjectImprovement(current, finding.SuggestedItem);
        return "Replace \"" + current.Title + "\"?\n\nCurrent\n"
            + FormatItem(currentView, current.Priority)
            + "\n\nProposed\n"
            + FormatItem(projected, current.Priority);
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
            Notes = suggested.Description?.Trim() ?? string.Empty,
            Acceptance = suggested.AcceptanceCriteria,
            ItemType = suggested.Type,
            IsCompleted = false
        };
    }

    private static string FormatItem(ReviewItemProjection item, ChecklistPriority priority)
    {
        var section = string.IsNullOrWhiteSpace(item.Category) ? "(no section)" : item.Category.Trim();
        var body = string.IsNullOrWhiteSpace(item.Notes) ? "No notes." : Clip(item.Notes.Trim(), 240);
        var text = Clip(item.Title?.Trim() ?? string.Empty, 120)
            + "\nSection: " + section
            + "\nPriority: " + priority
            + "\nNotes: " + body;
        if (!string.IsNullOrWhiteSpace(item.Acceptance))
            text += "\nAcceptance: " + Clip(item.Acceptance.Trim(), 180);
        if (!string.IsNullOrWhiteSpace(item.ItemType))
            text += "\nType: " + item.ItemType.Trim();
        return text;
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
