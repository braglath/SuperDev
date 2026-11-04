using System;
using System.Collections.Generic;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public sealed class ReviewApplyResult
{
    public bool Changed { get; init; }

    public string Message { get; init; } = string.Empty;

    public Guid? ItemId { get; init; }

    public static ReviewApplyResult None(string message) => new() { Message = message };

    public static ReviewApplyResult Updated(string message, Guid itemId) => new()
    {
        Changed = true,
        Message = message,
        ItemId = itemId
    };
}

public static class ChecklistReviewApplicator
{
    public static ReviewApplyResult Dismiss(ChecklistReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        finding.Dismissed = true;
        finding.Selected = false;
        return ReviewApplyResult.None("Dismissed.");
    }

    public static ReviewApplyResult Apply(IList<ChecklistItem> items, ChecklistReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.Dismissed)
            return ReviewApplyResult.None("This finding was dismissed.");

        if (finding.Applied)
            return ReviewApplyResult.None("This suggestion was already applied.");

        var result = finding.RecommendationType switch
        {
            ReviewRecommendationType.AddItem => ApplyAdd(items, finding),
            ReviewRecommendationType.ImproveItem => ApplyImprove(items, finding),
            ReviewRecommendationType.PriorityAdjustment => ApplyPriority(items, finding),
            _ => ReviewApplyResult.None("This finding is information only.")
        };

        if (result.Changed)
        {
            finding.Applied = true;
            finding.Selected = false;
        }

        return result;
    }

    public static ReviewApplyResult Revert(IList<ChecklistItem> items, ChecklistReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(finding);
        if (!finding.Applied)
            return ReviewApplyResult.None("This suggestion has not been applied.");

        var changed = false;
        Guid? itemId = null;
        foreach (var step in finding.UndoSteps)
        {
            if (step.RemoveItem)
            {
                var added = Find(items, [step.ItemId]);
                if (added is null)
                    continue;

                items.Remove(added);
                changed = true;
                itemId ??= added.Id;
                continue;
            }

            var current = Find(items, [step.ItemId]);
            if (current is null)
                continue;

            current.Title = step.Title;
            current.Category = step.Category;
            current.Notes = step.Notes;
            current.Priority = step.Priority;
            changed = true;
            itemId ??= current.Id;
        }

        finding.Applied = false;
        finding.UndoSteps.Clear();
        return changed
            ? ReviewApplyResult.Updated("Removed the applied suggestion.", itemId ?? Guid.Empty)
            : ReviewApplyResult.None("That change is no longer on this checklist.");
    }

    private static ReviewApplyResult ApplyAdd(IList<ChecklistItem> items, ChecklistReviewFinding finding)
    {
        var suggested = finding.SuggestedItem;
        if (suggested is null || string.IsNullOrWhiteSpace(suggested.Title))
            return ReviewApplyResult.None("This suggestion does not include a check to add.");

        var title = suggested.Title.Trim();
        foreach (var existing in items)
        {
            if (existing.Title.Trim().Equals(title, StringComparison.OrdinalIgnoreCase))
                return ReviewApplyResult.None("\"" + title + "\" is already on this checklist.");
        }

        var category = suggested.Section.Trim();
        if (category.Length == 0)
            category = CategoryOf(items, finding.RelatedItemIds);

        var item = ChecklistReviewMapper.ToNewItem(suggested, category);
        var insertAt = items.Count;
        if (category.Length > 0)
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (string.Equals(items[index].Category?.Trim(), category, StringComparison.OrdinalIgnoreCase))
                    insertAt = index + 1;
            }
        }

        items.Insert(insertAt, item);
        finding.UndoSteps =
        [
            new ReviewUndo
            {
                ItemId = item.Id,
                RemoveItem = true,
                Title = item.Title,
                Category = item.Category ?? string.Empty,
                Notes = item.Notes ?? string.Empty,
                Priority = item.Priority
            }
        ];
        return ReviewApplyResult.Updated("Added \"" + item.Title + "\".", item.Id);
    }

    private static ReviewApplyResult ApplyImprove(IList<ChecklistItem> items, ChecklistReviewFinding finding)
    {
        var targets = Related(items, finding.RelatedItemIds);
        if (targets.Count == 0)
            return ReviewApplyResult.None("That check is no longer on this checklist.");

        ChecklistPriority priority = default;
        var adjustPriority = finding.IncludesPriorityChange
            && ChecklistReviewMapper.TryPriority(finding.SuggestedItem?.Priority, out priority);
        var steps = new List<ReviewUndo>();
        foreach (var current in targets)
        {
            var projected = ChecklistReviewMapper.ProjectImprovement(current, finding.SuggestedItem);
            var priorityChanges = adjustPriority && current.Priority != priority;
            if (Same(current.Title, projected.Title)
                && Same(current.Category, projected.Category)
                && Same(current.Notes, projected.Notes)
                && !priorityChanges)
                continue;

            steps.Add(Snapshot(current));
            current.Title = projected.Title;
            current.Category = projected.Category;
            current.Notes = projected.Notes;
            if (priorityChanges)
                current.Priority = priority;
        }

        if (steps.Count == 0)
            return ReviewApplyResult.None("This check already matches the suggestion.");

        finding.UndoSteps = steps;
        var first = steps[0].ItemId;
        return steps.Count == 1
            ? ReviewApplyResult.Updated("Updated \"" + Find(items, [first])!.Title + "\".", first)
            : ReviewApplyResult.Updated("Updated " + steps.Count + " checks.", first);
    }

    private static ReviewApplyResult ApplyPriority(IList<ChecklistItem> items, ChecklistReviewFinding finding)
    {
        if (!ChecklistReviewMapper.TryPriority(finding.SuggestedItem?.Priority, out var priority))
            return ReviewApplyResult.None("The suggested priority is not supported.");

        var targets = Related(items, finding.RelatedItemIds);
        if (targets.Count == 0)
            return ReviewApplyResult.None("That check is no longer on this checklist.");

        var steps = new List<ReviewUndo>();
        foreach (var current in targets)
        {
            if (current.Priority == priority)
                continue;

            steps.Add(Snapshot(current));
            current.Priority = priority;
        }

        if (steps.Count == 0)
            return ReviewApplyResult.None("The priority is already " + priority + ".");

        finding.UndoSteps = steps;
        var first = Find(items, [steps[0].ItemId])!;
        return steps.Count == 1
            ? ReviewApplyResult.Updated("Updated the priority of \"" + first.Title + "\" to " + priority + ".", first.Id)
            : ReviewApplyResult.Updated("Updated the priority of " + steps.Count + " checks.", first.Id);
    }

    private static ReviewUndo Snapshot(ChecklistItem item) => new()
    {
        ItemId = item.Id,
        Title = item.Title ?? string.Empty,
        Category = item.Category ?? string.Empty,
        Notes = item.Notes ?? string.Empty,
        Priority = item.Priority
    };

    private static List<ChecklistItem> Related(IList<ChecklistItem> items, IReadOnlyList<Guid> ids)
    {
        var matches = new List<ChecklistItem>();
        foreach (var id in ids)
        {
            foreach (var item in items)
            {
                if (item.Id == id && !matches.Contains(item))
                    matches.Add(item);
            }
        }

        return matches;
    }

    private static ChecklistItem? Find(IList<ChecklistItem> items, IReadOnlyList<Guid> ids)
    {
        foreach (var id in ids)
        {
            foreach (var item in items)
            {
                if (item.Id == id)
                    return item;
            }
        }

        return null;
    }

    private static string CategoryOf(IList<ChecklistItem> items, IReadOnlyList<Guid> ids)
    {
        var related = Find(items, ids);
        return related?.Category?.Trim() ?? string.Empty;
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty, StringComparison.Ordinal);
}
