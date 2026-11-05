using System;
using System.Linq;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public static class TaskExplainMapper
{
    public static TaskExplainRequest Create(
        string? checklistTitle,
        WorkType workType,
        string? taskDescription,
        string? platform,
        string? framework,
        ChecklistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var (notes, acceptance) = ChecklistReviewMapper.ReadDetails(item);
        return new TaskExplainRequest
        {
            ChecklistTitle = checklistTitle?.Trim() ?? string.Empty,
            WorkType = workType,
            TaskDescription = Blank(taskDescription),
            Platform = Blank(platform),
            Framework = Blank(framework),
            Title = string.IsNullOrWhiteSpace(item.Title) ? "Untitled check" : item.Title.Trim(),
            Category = item.Category?.Trim() ?? string.Empty,
            Priority = item.Priority.ToString(),
            Notes = notes,
            Acceptance = Blank(acceptance),
            ItemType = item.ItemType,
            TestReports = Blank(item.TestReports),
            Logs = Blank(item.Logs),
            CodeReferences = Blank(item.CodeReferences),
            ApiResponses = Blank(item.ApiResponses),
            PullRequestLinks = Blank(item.PullRequestLinks),
            IsCompleted = item.IsCompleted && !item.IsSkipped,
            IsSkipped = item.IsSkipped,
            Subtasks = item.ManualSubtasks.Select(Child).ToList(),
            Links = item.LinkedSubtasks.Select(Child).ToList()
        };
    }

    private static TaskExplainChild Child(ChecklistSubtask subtask) => new()
    {
        Title = subtask.Title?.Trim() ?? string.Empty,
        IsCompleted = subtask.IsCompleted,
        Optional = subtask.IsLink && subtask.LinkOptional
    };

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
