using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public sealed class ChecklistReviewPromptBuilder
{
    public const int MaxFindings = 10;

    public string SystemPrompt { get; } =
        """
        Reply with JSON only. No markdown.
        Review the checklist as a senior software engineer.
        Report only real gaps. Do not regenerate the checklist or invent requirements.
        Do not suggest a check that already exists. Use an empty findings list when an area has no gap.
        Return at most 10 findings, highest risk first.
        Always return qualityScore and summary.
        qualityScore is an integer from 0 to 100. Use 90-100 when checks are specific, distinct, and cover the task. Lower it for missing checks, duplicates, vague checks, unstated risks, or missing validation.
        summary is one sentence on overall checklist quality.
        Assess these areas:
        Missing checks: category Completeness, recommendationType AddItem.
        Duplicate checks: category Duplicates, recommendationType PossibleDuplicate, with both ids in relatedItemIds.
        Vague or incomplete checks: category Clarity, recommendationType ImproveItem. A check is vague when it names no observable result.
        Potential risks: category Risk. Cover failure, data-loss, auth, and regression risks the checks leave open.
        Additional validation: category Testing, recommendationType AddItem, for a concrete check that proves the work.
        category is Completeness, Duplicates, Clarity, Testing, Security, Performance, Priority, or Risk.
        severity is Critical, High, Medium, or Low.
        recommendationType is AddItem, ImproveItem, PossibleDuplicate, PriorityAdjustment, or Observation.
        AddItem adds one new check. suggestedItem.title is that check, never an instruction such as "Add this to item i2".
        ImproveItem edits one existing check named in relatedItemIds. Keep its title unless the title itself is wrong.
        An open check is unfinished work, not a missing check. Do not add a new check because one is still open.
        PriorityAdjustment only changes priority. PossibleDuplicate names the overlap and does not delete checks.
        Observation changes nothing. Security only for auth, data, input, or secrets. Performance only for speed or memory.
        relatedItemIds are ids such as i1. priority is Critical, Required, Conditional, or Review.
        type is Investigation, Implementation, Verification, or Documentation.
        title, explanation, whyItMatters, and suggestedAction are one short sentence each.
        Use only the platform, framework, and task lines that are present. Do not guess missing ones, and do not report them as findings.
        Test reports, logs, code references, API responses, and PR links already written on an item are evidence. Do not invent any, and do not add a check just to collect evidence that is already there.
        """;

    public Prompt Build(ChecklistReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = new StringBuilder();
        user.AppendLine(Focus(request.WorkType));
        user.AppendLine("Checklist: " + Shown(request.Title, "Untitled checklist"));
        AppendDetail(user, "Task", request.TaskDescription);
        AppendDetail(user, "Platform", request.Platform);
        AppendDetail(user, "Framework", request.Framework);
        user.AppendLine("Work type: " + WorkLabel(request.WorkType));
        user.AppendLine("Sections: " + Sections(request.Items));
        user.AppendLine("Item type is omitted when this checklist did not store one.");
        user.AppendLine("Completed checks stay completed. Do not suggest removing them.");
        user.AppendLine("Items:");
        AppendItems(user, request.Items);
        user.Append("Return findings, a qualityScore from 0 to 100, and a one-sentence summary for this checklist only.");
        return new Prompt(SystemPrompt, user.ToString());
    }

    private static void AppendItems(StringBuilder user, IReadOnlyList<ChecklistReviewItem> items)
    {
        if (items.Count == 0)
        {
            user.AppendLine("none");
            return;
        }

        var detailed = Math.Min(items.Count, 24);
        var listed = Math.Min(items.Count, 48);
        for (var index = 0; index < listed; index++)
        {
            var item = items[index];
            user.Append(item.Alias);
            user.Append(" | ");
            user.Append(string.IsNullOrWhiteSpace(item.Section) ? "(no section)" : Compact(item.Section, 40));
            user.Append(" | ");
            user.Append(Compact(item.Title, 80));
            user.Append(" | ");
            user.Append(string.IsNullOrWhiteSpace(item.Priority) ? "Required" : Compact(item.Priority, 20));
            user.Append(" | ");
            user.Append(item.IsSkipped ? "skipped" : item.IsCompleted ? "done" : "open");
            if (!string.IsNullOrWhiteSpace(item.ItemType))
            {
                user.Append(" | type ");
                user.Append(Compact(item.ItemType, 40));
            }

            if (index < detailed)
            {
                if (!string.IsNullOrWhiteSpace(item.Description))
                {
                    user.Append(" | ");
                    user.Append(Compact(item.Description, 140));
                }

                if (!string.IsNullOrWhiteSpace(item.AcceptanceCriteria))
                {
                    user.Append(" | Acceptance: ");
                    user.Append(Compact(item.AcceptanceCriteria, 120));
                }

                AppendEvidence(user, "Test reports", item.TestReports);
                AppendEvidence(user, "Logs", item.Logs);
                AppendEvidence(user, "Code references", item.CodeReferences);
                AppendEvidence(user, "API responses", item.ApiResponses);
                AppendEvidence(user, "PR links", item.PullRequestLinks);
            }

            user.AppendLine();
        }

        if (items.Count > listed)
            user.AppendLine("And " + (items.Count - listed) + " more items are on the checklist.");
    }

    private static void AppendEvidence(StringBuilder user, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        user.Append(" | ");
        user.Append(label);
        user.Append(": ");
        user.Append(Compact(value, 100));
    }

    private static string Sections(IReadOnlyList<ChecklistReviewItem> items)
    {
        var names = items
            .Select(item => item.Section.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    private static void AppendDetail(StringBuilder user, string label, string? value)
    {
        var compact = Compact(value, 400);
        if (compact.Length > 0)
            user.AppendLine(label + ": " + compact);
    }

    private static string Shown(string? value, string fallback)
    {
        var compact = Compact(value, 400);
        return compact.Length == 0 ? fallback : compact;
    }

    private static string Focus(WorkType work) => work switch
    {
        WorkType.Bug => "Bug fix: the behavior already exists. Look for the cause, the fix, and regression coverage. Do not invent a new feature.",
        WorkType.Feature => "New feature: review only the requested behavior, including empty, error, and success cases.",
        WorkType.Development => "Development: review the described work. Do not add unrelated features.",
        WorkType.Testing => "Testing: look for missing functional, edge-case, and regression checks. Do not add implementation work.",
        WorkType.PullRequest => "Code review: look for correctness, regressions, and missing tests.",
        WorkType.Daily => "Daily checklist: review only the work that is already listed.",
        WorkType.Blank => "Review only the checks that are written down.",
        _ => "Custom work: stay on the checks that are written down."
    };

    private static string WorkLabel(WorkType work) => work switch
    {
        WorkType.Bug => "Bug Fix",
        WorkType.Feature => "Feature",
        WorkType.Development => "Development",
        WorkType.Testing => "Testing",
        WorkType.PullRequest => "Pull Request",
        WorkType.Daily => "Daily",
        WorkType.Blank => "Blank",
        _ => "Custom"
    };

    private static string Compact(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || max <= 0)
            return string.Empty;

        var compact = new StringBuilder(Math.Min(value.Length, max));
        var pendingSpace = false;
        foreach (var character in value.Trim())
        {
            if (character == '|')
            {
                pendingSpace = compact.Length > 0;
                continue;
            }

            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = compact.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                if (compact.Length + 1 >= max)
                    break;
                compact.Append(' ');
                pendingSpace = false;
            }

            if (compact.Length + 1 > max)
                break;

            compact.Append(character);
        }

        return compact.ToString();
    }
}
