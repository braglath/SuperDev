using System;
using System.Collections.Generic;
using System.Linq;
using SuperDev.Models;
using SuperDev.Services.Ai;

namespace SuperDev.Services;

/// <summary>
/// Plain-text and CSV views of a checklist for the clipboard and for export.
/// </summary>
public static class ChecklistShare
{
    public static string ToPlainText(ChecklistDocument document)
    {
        var lines = new List<string> { DisplayTitle(document) };
        if (!string.IsNullOrWhiteSpace(document.Description))
            lines.Add(document.Description.Trim());

        var workflow = WorkflowName(document);
        if (workflow.Length > 0)
            lines.Add(workflow);

        lines.Add(document.IsCompleted ? "Status: Completed" : "Status: In progress");

        var progress = Progress(document);
        if (progress is not null)
            lines.Add($"Progress: {progress}");

        AddLabeled(lines, "Deadline", DeadlineText(
            document.Deadline,
            document.DeadlineMinute,
            document.NotifyBeforeMinutes,
            document.DeadlineSilent));
        AddLabeled(lines, "Reminder", ReminderLabel(document));
        AddLabeled(lines, "Branch", document.BranchName);
        AddLabeled(lines, "PR", document.PullRequestUrl);

        if (document.Items.Count > 0)
        {
            lines.Add(string.Empty);
            for (var index = 0; index < document.Items.Count; index++)
            {
                if (index > 0)
                    lines.Add(string.Empty);

                AppendItem(lines, document.Items[index]);
            }
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static string ToCsv(ChecklistDocument document, string? fileName)
    {
        var progress = ChecklistCounts.Measure(document.Items);
        var rows = new List<string>
        {
            Csv("Details"),
            Detail("File name", fileName),
            Detail("Workflow", WorkflowName(document)),
            Detail("Description", document.Description),
            Detail("Status", document.IsCompleted ? "Completed" : "In progress"),
            Detail("Progress", progress.ShareLabel),
            Detail("Branch", document.BranchName),
            Detail("Pull Request", document.PullRequestUrl),
            string.Empty,
            Row("Check", "Status", "Category", "Priority", "Type", "Deadline", "Subtasks", "Notes", "Acceptance", "Test reports", "Logs", "Code references", "API responses", "PR links", "Evidence")
        };

        foreach (var item in document.Items)
        {
            rows.Add(Row(
                item.Title,
                ItemStatus(item),
                item.Category,
                item.Priority.ToString(),
                item.ItemType ?? string.Empty,
                DeadlineText(item.Deadline, item.DeadlineMinute, item.NotifyBeforeMinutes, item.DeadlineSilent),
                SubtaskCell(item),
                item.Notes,
                item.Acceptance ?? string.Empty,
                item.TestReports ?? string.Empty,
                item.Logs ?? string.Empty,
                item.CodeReferences ?? string.Empty,
                item.ApiResponses ?? string.Empty,
                item.PullRequestLinks ?? string.Empty,
                EvidenceCell(item)));
        }

        return string.Join(Environment.NewLine, rows) + Environment.NewLine;
    }

    private static void AppendItem(List<string> lines, ChecklistItem item)
    {
        var mark = item.IsSkipped ? "-" : item.IsCompleted ? "x" : " ";
        var title = string.IsNullOrWhiteSpace(item.Title) ? "Untitled check" : item.Title.Trim();
        var detail = item.IsSkipped ? $"{item.Detail} · Skipped" : item.Detail;
        lines.Add($"- [{mark}] {title} — {detail}");

        var deadline = DeadlineText(item.Deadline, item.DeadlineMinute, item.NotifyBeforeMinutes, item.DeadlineSilent);
        if (deadline.Length > 0)
            lines.Add($"  Deadline: {deadline}");

        if (item.HasItemType)
            lines.Add("  Type: " + item.ItemType);

        if (!string.IsNullOrWhiteSpace(item.Notes))
        {
            lines.Add("  Notes:");
            foreach (var noteLine in item.Notes.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                lines.Add("  " + noteLine);
        }

        if (item.HasAcceptance)
        {
            lines.Add("  Acceptance:");
            foreach (var line in item.Acceptance!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                lines.Add("  " + line);
        }

        foreach (var reference in item.References)
        {
            lines.Add("  " + reference.Label + ":");
            foreach (var line in reference.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                lines.Add("  " + line);
        }

        foreach (var line in EvidenceLines(item))
            lines.Add("  " + line);

        foreach (var subtask in item.Subtasks)
            lines.Add("  - " + SubtaskLine(subtask));
    }

    private static string EvidenceCell(ChecklistItem item) =>
        string.Join("\n", EvidencePaths(item));

    private static IEnumerable<string> EvidenceLines(ChecklistItem item)
    {
        var paths = EvidencePaths(item).ToList();
        if (paths.Count == 0)
            yield break;

        yield return "Evidence:";
        foreach (var path in paths)
            yield return path;
    }

    private static IEnumerable<string> EvidencePaths(ChecklistItem item)
    {
        foreach (var file in item.Evidence ?? [])
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path))
                continue;

            var path = file.Path.Trim();
            yield return EvidenceKind.FileExists(path)
                ? path
                : "missing: " + path;
        }
    }

    private static string SubtaskCell(ChecklistItem item)
    {
        if (item.Subtasks.Count == 0)
            return string.Empty;

        return string.Join("\n", item.Subtasks.Select(SubtaskLine));
    }

    private static string SubtaskLine(ChecklistSubtask subtask)
    {
        var mark = subtask.IsCompleted ? "x" : " ";
        var kind = !subtask.IsLink
            ? string.Empty
            : subtask.LinkOptional ? " (optional link)" : " (required link)";
        return $"[{mark}] {subtask.Title}{kind}";
    }

    private static string ItemStatus(ChecklistItem item) =>
        item.IsSkipped ? "Skipped" : item.IsCompleted ? "Completed" : "Open";

    private static string? Progress(ChecklistDocument document)
    {
        if (document.Items.Count == 0)
            return null;

        return ChecklistCounts.Measure(document.Items).ShareLabel;
    }

    private static string? ReminderLabel(ChecklistDocument document)
    {
        if (document.ReminderOn is null)
            return null;

        var label = ReminderText.ButtonLabel(
            document.ReminderOn,
            document.ReminderEveryMinutes,
            document.ReminderSilent);
        const string prefix = "Reminder: ";
        return label.StartsWith(prefix, StringComparison.Ordinal)
            ? label[prefix.Length..]
            : label;
    }

    private static string DeadlineText(DateOnly? deadline, int? minuteOfDay, int? minutesBefore, bool silent)
    {
        if (deadline is not { } date)
            return string.Empty;

        var text = DeadlineValue.Format(date, minuteOfDay);
        var notify = DeadlineValue.NotifyText(minutesBefore);
        if (notify.Length > 0)
            text = $"{text} · {notify}";

        return silent && minutesBefore is not null ? $"{text} · Silent" : text;
    }

    private static string DisplayTitle(ChecklistDocument document) =>
        string.IsNullOrWhiteSpace(document.Title) ? "Untitled" : document.Title.Trim();

    private static string WorkflowName(ChecklistDocument document)
    {
        var named = AiChecklistMapper.VisibleTemplateName(document.TemplateName);
        if (named.Length > 0)
            return named;

        return document.WorkType switch
        {
            WorkType.Bug => "Bug Fix",
            WorkType.Feature => "Feature",
            WorkType.Development => "Development",
            WorkType.Testing => "Testing",
            WorkType.PullRequest => "Pull Request",
            WorkType.Blank => "Blank Checklist",
            WorkType.Daily => "Daily",
            WorkType.Custom => "Custom",
            _ => string.Empty
        };
    }

    private static void AddLabeled(List<string> lines, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        lines.Add($"{label}: {value.Trim()}");
    }

    private static string Detail(string label, string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        return Csv($"{label}: {text}");
    }

    private static string Row(params string?[] cells) =>
        string.Join(",", cells.Select(Csv));

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        var quoted = text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r');
        if (!quoted)
            return text;

        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
