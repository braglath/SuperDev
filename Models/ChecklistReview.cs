using System;
using System.Collections.Generic;
using System.Linq;

namespace SuperDev.Models;

public enum ReviewCategory
{
    Completeness,
    Duplicates,
    Clarity,
    Testing,
    Security,
    Performance,
    Priority,
    Risk
}

public enum ReviewSeverity
{
    Critical,
    High,
    Medium,
    Low
}

public enum ReviewRecommendationType
{
    AddItem,
    ImproveItem,
    PossibleDuplicate,
    PriorityAdjustment,
    Observation
}

/// <summary>
/// One checklist row sent to a review. Fields that were never stored stay empty.
/// </summary>
public sealed class ChecklistReviewItem
{
    public Guid Id { get; init; }

    public string Alias { get; init; } = string.Empty;

    public string Section { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string AcceptanceCriteria { get; init; } = string.Empty;

    public string Priority { get; init; } = string.Empty;

    /// <summary>
    /// Item type when the checklist stored one. Manual checks leave this empty.
    /// </summary>
    public string? ItemType { get; init; }

    public string TestReports { get; init; } = string.Empty;

    public string Logs { get; init; } = string.Empty;

    public string CodeReferences { get; init; } = string.Empty;

    public string ApiResponses { get; init; } = string.Empty;

    public string PullRequestLinks { get; init; } = string.Empty;

    public bool IsCompleted { get; init; }

    public bool IsSkipped { get; init; }
}

public sealed class ChecklistReviewRequest
{
    public string Title { get; init; } = string.Empty;

    public string? TaskDescription { get; init; }

    public string? Platform { get; init; }

    public string? Framework { get; init; }

    public WorkType WorkType { get; init; }

    public IReadOnlyList<ChecklistReviewItem> Items { get; init; } = [];
}

public sealed class SuggestedChecklistItem
{
    public string Title { get; set; } = string.Empty;

    public string Section { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string AcceptanceCriteria { get; set; } = string.Empty;

    public string Priority { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// When set, <see cref="Description"/> is added to the existing notes.
    /// The existing title stays as it is.
    /// </summary>
    public bool AppendNote { get; set; }
}

/// <summary>
/// Checklist fields captured before a suggestion is applied, so the change can be removed.
/// </summary>
public sealed class ReviewUndo
{
    public Guid ItemId { get; init; }

    public bool RemoveItem { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    public string Acceptance { get; init; } = string.Empty;

    public string ItemType { get; init; } = string.Empty;

    public ChecklistPriority Priority { get; init; }
}

public sealed class ChecklistReviewFinding
{
    public string Id { get; set; } = string.Empty;

    public ReviewCategory Category { get; set; }

    public ReviewSeverity Severity { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Explanation { get; set; } = string.Empty;

    public string WhyItMatters { get; set; } = string.Empty;

    public string SuggestedAction { get; set; } = string.Empty;

    public SuggestedChecklistItem? SuggestedItem { get; set; }

    public List<Guid> RelatedItemIds { get; set; } = [];

    public ReviewRecommendationType RecommendationType { get; set; }

    public bool Dismissed { get; set; }

    public bool Selected { get; set; }

    public bool Applied { get; set; }

    /// <summary>
    /// Set when this finding also includes a priority change for the same check.
    /// </summary>
    public bool IncludesPriorityChange { get; set; }

    public List<ReviewUndo> UndoSteps { get; set; } = [];

    public bool CanApply =>
        !Dismissed
        && !Applied
        && RecommendationType is ReviewRecommendationType.AddItem
            or ReviewRecommendationType.ImproveItem
            or ReviewRecommendationType.PriorityAdjustment;
}

public sealed class ChecklistReviewResult
{
    public string ChecklistTitle { get; set; } = string.Empty;

    /// <summary>
    /// Overall checklist quality from 0 to 100. Higher means the checks are specific, distinct, and cover the task.
    /// </summary>
    public int QualityScore { get; set; }

    /// <summary>
    /// One sentence on missing checks, duplicates, vague checks, risks, and validation.
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    public List<ChecklistReviewFinding> Findings { get; set; } = [];

    public int ActiveCount => Findings.Count(finding => !finding.Dismissed);

    public bool AllowsBulkSelection =>
        Findings.Count(finding => !finding.Dismissed && finding.CanApply) > 1;

    public bool HasSelectedSuggestion =>
        Findings.Any(finding => !finding.Dismissed && finding.Selected && finding.CanApply);

    public int SelectApplicable()
    {
        var selected = 0;
        foreach (var finding in Findings)
        {
            finding.Selected = finding.CanApply;
            if (finding.Selected)
                selected++;
        }

        return selected;
    }
}

public sealed class ChecklistReviewInspection
{
    public bool IsValid { get; init; }

    public ChecklistReviewResult? Review { get; init; }

    public AiFailureKind? FailureKind { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class ChecklistReviewOutcome
{
    public ChecklistReviewResult? Review { get; init; }

    public AiFailure? Failure { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool Succeeded => Failure is null && Review is not null;
}
