using System;
using System.Collections.Generic;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public static class AiChecklistMapper
{
    public static WorkType ToWorkType(AiWorkKind workKind) => workKind switch
    {
        AiWorkKind.BugFix => WorkType.Bug,
        AiWorkKind.NewFeature => WorkType.Feature,
        AiWorkKind.Development => WorkType.Development,
        AiWorkKind.Testing => WorkType.Testing,
        AiWorkKind.CodeReview => WorkType.PullRequest,
        _ => WorkType.Custom
    };

    public const string TemplatePrefix = "AI · ";

    public static string TemplateName(AiChecklistRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return TemplatePrefix + AiCatalog.FrameworkLabel(request.Framework) + " · " + AiCatalog.WorkLabel(request.WorkKind);
    }

    /// <summary>
    /// Workflow line shown under a tasklist title. The stored name keeps the AI prefix
    /// so older files can still be recognized. The page does not show that prefix.
    /// </summary>
    public static string VisibleTemplateName(string? templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName))
            return string.Empty;

        var name = templateName.Trim();
        const string marker = "AI ·";
        if (name.Equals("AI", StringComparison.Ordinal) || name.Equals(marker, StringComparison.Ordinal))
            return string.Empty;

        if (name.StartsWith(TemplatePrefix, StringComparison.Ordinal))
            name = name[TemplatePrefix.Length..].Trim();

        return name;
    }

    public static List<ChecklistItem> ToItems(GeneratedChecklist checklist)
    {
        ArgumentNullException.ThrowIfNull(checklist);
        var items = new List<ChecklistItem>();
        foreach (var section in checklist.Sections)
        {
            var category = section.Name.Trim();
            foreach (var item in section.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Title))
                    continue;

                items.Add(new ChecklistItem
                {
                    Title = item.Title.Trim(),
                    Category = category,
                    Priority = ToPriority(item),
                    Notes = item.Description.Trim(),
                    Acceptance = item.AcceptanceCriteria,
                    ItemType = item.Type,
                    TestReports = item.TestReports,
                    Logs = item.Logs,
                    CodeReferences = item.CodeReferences,
                    ApiResponses = item.ApiResponses,
                    PullRequestLinks = item.PullRequestLinks
                });
            }
        }

        return items;
    }

    public static ChecklistPriority ToPriority(GeneratedCheckItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!ChecklistValidator.TryNormalizePriority(item.Priority, out var priority))
            return ChecklistPriority.Required;

        return priority switch
        {
            "Critical" => ChecklistPriority.Critical,
            "Conditional" => ChecklistPriority.Conditional,
            "Review" => ChecklistPriority.Review,
            _ => ChecklistPriority.Required
        };
    }

    public static string ToNotes(GeneratedCheckItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var description = item.Description.Trim();
        var acceptance = item.AcceptanceCriteria.Trim();
        if (description.Length == 0)
            return acceptance.Length == 0 ? string.Empty : "Acceptance: " + acceptance;

        if (acceptance.Length == 0)
            return description;

        return description + "\n\nAcceptance: " + acceptance;
    }
}
