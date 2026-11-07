using System;
using System.Collections.Generic;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public static class ChecklistSummary
{
    public const int MaxComposedLength = 1200;

    public static string ForChecklist(string? summary, string? task, GeneratedChecklist checklist)
    {
        ArgumentNullException.ThrowIfNull(checklist);
        var current = summary?.Trim() ?? string.Empty;
        if (current.Length > 0 && !MatchesTask(current, task))
            return current;

        var composed = Compose(checklist);
        if (composed.Length == 0 || !MatchesTask(composed, task))
            return composed;

        return Fit("Checks: " + composed.Replace("\n", " ", StringComparison.Ordinal));
    }

    public static bool MatchesTask(string? summary, string? task)
    {
        var left = Compact(summary);
        var right = Compact(task);
        if (left.Length == 0 || right.Length == 0)
            return false;

        if (string.Equals(TrimSentenceEnd(left), TrimSentenceEnd(right), StringComparison.OrdinalIgnoreCase))
            return true;

        return left.Length >= ChecklistValidator.MaxSummaryLength - 1
            && right.StartsWith(left, StringComparison.OrdinalIgnoreCase);
    }

    private static string Compose(GeneratedChecklist checklist)
    {
        var sections = new List<string>();
        foreach (var section in checklist.Sections)
        {
            var checks = new List<string>();
            foreach (var item in section.Items)
            {
                var check = CheckText(item);
                if (check.Length > 0)
                    checks.Add(check);
            }

            if (checks.Count == 0)
                continue;

            var name = Compact(section.Name);
            if (name.Length == 0)
                name = "Checks";
            sections.Add(name + ": " + string.Join(". ", checks) + ".");
        }

        return sections.Count == 0 ? string.Empty : Fit(string.Join("\n", sections));
    }

    private static string CheckText(GeneratedCheckItem item)
    {
        var title = Compact(item.Title);
        if (title.Length == 0)
            return string.Empty;

        var detail = Compact(item.Description);
        if (detail.Length == 0)
            detail = Compact(item.AcceptanceCriteria);
        if (detail.Length == 0)
            return title;

        return title + " — " + TrimSentenceEnd(detail);
    }

    private static string Fit(string text)
    {
        if (text.Length <= MaxComposedLength)
            return text;

        var window = text[..MaxComposedLength];
        var cut = window.LastIndexOf('\n');
        if (cut < 40)
            cut = window.LastIndexOf(". ", StringComparison.Ordinal);
        if (cut < 40)
            cut = window.LastIndexOf(' ');
        if (cut < 40)
            return window.TrimEnd();

        var end = window[cut] == '.' ? cut + 1 : cut;
        return window[..end].TrimEnd();
    }

    private static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string TrimSentenceEnd(string value) =>
        value.TrimEnd('.', '!', '?', '…', ' ');
}
