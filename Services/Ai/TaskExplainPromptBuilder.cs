using System;
using System.Collections.Generic;
using System.Text;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public sealed class TaskExplainPromptBuilder
{
    public const int MaxNotes = 700;

    public string SystemPrompt { get; } =
        """
        Reply with JSON only. No markdown.
        Explain one checklist item for the person who has to do the work.
        Say what the check is asking, why it is on the list, the concrete steps, and when it is done.
        Use the title, notes, acceptance, type, subtasks, and checklist context. Do not invent a different task.
        If acceptance is written, that is when the check is done.
        When test reports, logs, code references, API responses, or PR links are written, use them. Do not invent any.
        If the notes already say how, explain those notes. Do not add work they do not support.
        summary, whyItMatters, and doneWhen are one or two short sentences each.
        steps is 2 to 5 short actions. Each step is one sentence.
        A completed check is already done. Explain what it asked for, and do not tell them to do it again.
        Do not tell them to mark the check complete.
        """;

    public Prompt Build(TaskExplainRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = new StringBuilder();
        user.AppendLine(Focus(request.WorkType));
        user.AppendLine("Checklist: " + Shown(request.ChecklistTitle, "Untitled checklist", 200));
        AppendDetail(user, "Task", request.TaskDescription);
        AppendDetail(user, "Platform", request.Platform);
        AppendDetail(user, "Framework", request.Framework);
        user.AppendLine("Work type: " + WorkLabel(request.WorkType));
        user.AppendLine("Check: " + Shown(request.Title, "Untitled check", 200));
        user.AppendLine("Category: " + Shown(request.Category, "General", 80));
        user.AppendLine("Priority: " + Shown(request.Priority, "Required", 40));
        user.AppendLine("Status: " + (request.IsCompleted ? "completed" : "open"));
        user.AppendLine("Notes: " + Shown(request.Notes, "No notes.", MaxNotes));
        user.AppendLine("Acceptance: " + Shown(request.Acceptance, "None.", MaxNotes));
        user.AppendLine("Type: " + Shown(request.ItemType, "None.", 40));
        AppendDetail(user, "Test reports", request.TestReports);
        AppendDetail(user, "Logs", request.Logs);
        AppendDetail(user, "Code references", request.CodeReferences);
        AppendDetail(user, "API responses", request.ApiResponses);
        AppendDetail(user, "PR links", request.PullRequestLinks);
        AppendChildren(user, "Subtasks", request.Subtasks, links: false);
        AppendChildren(user, "Linked checklists", request.Links, links: true);
        user.Append("Explain this check only.");
        return new Prompt(SystemPrompt, user.ToString());
    }

    private static void AppendChildren(
        StringBuilder user,
        string label,
        IReadOnlyList<TaskExplainChild> children,
        bool links)
    {
        if (children.Count == 0)
        {
            user.AppendLine(label + ": none");
            return;
        }

        user.AppendLine(label + ":");
        var shown = 0;
        foreach (var child in children)
        {
            if (shown == 8)
                break;

            var title = Compact(child.Title, 80);
            if (title.Length == 0)
                title = "Untitled";

            shown++;
            user.Append("- ");
            user.Append(title);
            user.Append(" (");
            if (links)
                user.Append(child.Optional ? "optional, " : "required, ");
            user.Append(child.IsCompleted ? "done" : "open");
            user.AppendLine(")");
        }

        if (children.Count > shown)
            user.AppendLine("And " + (children.Count - shown) + " more.");
    }

    private static void AppendDetail(StringBuilder user, string label, string? value)
    {
        var compact = Compact(value, 400);
        if (compact.Length > 0)
            user.AppendLine(label + ": " + compact);
    }

    private static string Shown(string? value, string fallback, int max)
    {
        var compact = Compact(value, max);
        return compact.Length == 0 ? fallback : compact;
    }

    private static string Focus(WorkType work) => work switch
    {
        WorkType.Bug => "Bug fix: the behavior already exists. Explain the cause, the fix, or the regression check that is written down.",
        WorkType.Feature => "New feature: explain only the requested behavior.",
        WorkType.Development => "Development: explain the check that is written down.",
        WorkType.Testing => "Testing: explain how to verify the check. Do not turn it into implementation work.",
        WorkType.PullRequest => "Code review: explain what this check is asking the reviewer to look at.",
        WorkType.Daily => "Daily checklist: explain only this check.",
        WorkType.Blank => "Explain only what is written on this check.",
        _ => "Custom work: explain only what is written on this check."
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
