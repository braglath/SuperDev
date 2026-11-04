using System;
using System.Collections.Generic;
using System.Linq;
using SuperDev.Models;

namespace SuperDev.Services;

/// <summary>
/// Plain-text and CSV views of a checklist for the clipboard and for export.
/// </summary>
public static class ChecklistShare
{
    public static string ToPlainText(ChecklistDocument document)
    {
        var lines = new List<string> { DisplayTitle(document) };
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
        var done = document.Items.Count(item => item.IsCompleted);
        var rows = new List<string>
        {
            Csv("Details"),
            Detail("File name", fileName),
            Detail("Workflow", WorkflowName(document)),
            Detail("Status", document.IsCompleted ? "Completed" : "In progress"),
            Detail("Progress", $"{done} of {document.Items.Count} complete"),
            Detail("Branch", document.BranchName),
            Detail("Pull Request", document.PullRequestUrl),
            string.Empty,
            Row("Check", "Status", "Category", "Priority", "Deadline", "Subtasks", "Notes")
        };

        foreach (var item in document.Items)
        {
            rows.Add(Row(
                item.Title,
                item.IsCompleted ? "Completed" : "Open",
                item.Category,
                item.Priority.ToString(),
                DeadlineText(item.Deadline, item.DeadlineMinute, item.NotifyBeforeMinutes, item.DeadlineSilent),
                SubtaskCell(item),
                item.Notes));
        }

        return string.Join(Environment.NewLine, rows) + Environment.NewLine;
    }

    private static void AppendItem(List<string> lines, ChecklistItem item)
    {
        var mark = item.IsCompleted ? "x" : " ";
        var title = string.IsNullOrWhiteSpace(item.Title) ? "Untitled check" : item.Title.Trim();
        lines.Add($"- [{mark}] {title} — {item.Detail}");

        var deadline = DeadlineText(item.Deadline, item.DeadlineMinute, item.NotifyBeforeMinutes, item.DeadlineSilent);
        if (deadline.Length > 0)
            lines.Add($"  Deadline: {deadline}");

        if (!string.IsNullOrWhiteSpace(item.Notes))
        {
            lines.Add("  Notes:");
            foreach (var noteLine in item.Notes.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                lines.Add("  " + noteLine);
        }

        foreach (var subtask in item.Subtasks)
        {
            var subtaskMark = subtask.IsCompleted ? "x" : " ";
            lines.Add($"  - [{subtaskMark}] {subtask.Title}");
        }
    }

    private static string SubtaskCell(ChecklistItem item)
    {
        if (item.Subtasks.Count == 0)
            return string.Empty;

        return string.Join("\n", item.Subtasks.Select(subtask =>
        {
            var mark = subtask.IsCompleted ? "x" : " ";
            return $"[{mark}] {subtask.Title}";
        }));
    }

    private static string? Progress(ChecklistDocument document)
    {
        if (document.Items.Count == 0)
            return null;

        var done = document.Items.Count(item => item.IsCompleted);
        return $"{done} of {document.Items.Count} complete";
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
        if (!string.IsNullOrWhiteSpace(document.TemplateName))
            return document.TemplateName.Trim();

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
