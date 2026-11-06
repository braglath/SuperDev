using System.Collections.Generic;
using SuperDev.Models;

namespace SuperDev.Services;

public enum TasklistRunState
{
    NotStarted,
    InProgress,
    Completed
}

/// <summary>
/// One tasklist reduced to the same check counts <see cref="ChecklistCounts"/> uses.
/// </summary>
public readonly record struct TasklistProgressInput(
    bool DocumentCompleted,
    int Completed,
    int Total,
    int Skipped)
{
    public static TasklistProgressInput FromItems(
        bool documentCompleted,
        IEnumerable<ChecklistItem> items)
    {
        var progress = ChecklistCounts.Measure(items);
        return new TasklistProgressInput(
            documentCompleted,
            progress.Completed,
            progress.Total,
            progress.Skipped);
    }
}

public readonly record struct ProjectProgressReport(
    int TasklistCount,
    int InProgress,
    int Completed,
    int NotStarted,
    int ChecksCompleted,
    int ChecksTotal,
    int ChecksSkipped,
    double Percent)
{
    public static ProjectProgressReport Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    public string PercentLabel =>
        ChecksTotal == 0 ? "0%" : $"{Percent:0}%";
}

/// <summary>
/// Project completion is the combined check count across tasklists.
/// Tasklist percentages are not averaged.
/// Skipped checks are left out. Optional and blocked checks follow
/// <see cref="ChecklistCounts"/>: a skipped check is excluded, a completed
/// check counts, and an open check stays in the total whether or not it
/// blocks finishing the checklist. Optional links are subtasks, not checks.
/// </summary>
public static class ProjectProgress
{
    public static TasklistRunState StateOf(TasklistProgressInput tasklist)
    {
        if (tasklist.DocumentCompleted)
            return TasklistRunState.Completed;

        if (tasklist.Total > 0 && tasklist.Completed == tasklist.Total)
            return TasklistRunState.Completed;

        if (tasklist.Completed <= 0)
            return TasklistRunState.NotStarted;

        return TasklistRunState.InProgress;
    }

    public static ProjectProgressReport Measure(IEnumerable<TasklistProgressInput> tasklists)
    {
        var count = 0;
        var inProgress = 0;
        var completed = 0;
        var notStarted = 0;
        var checksCompleted = 0;
        var checksTotal = 0;
        var checksSkipped = 0;

        foreach (var tasklist in tasklists)
        {
            count++;
            switch (StateOf(tasklist))
            {
                case TasklistRunState.Completed:
                    completed++;
                    break;
                case TasklistRunState.InProgress:
                    inProgress++;
                    break;
                default:
                    notStarted++;
                    break;
            }

            if (tasklist.Completed > 0)
                checksCompleted += tasklist.Completed;
            if (tasklist.Total > 0)
                checksTotal += tasklist.Total;
            if (tasklist.Skipped > 0)
                checksSkipped += tasklist.Skipped;
        }

        var percent = checksTotal == 0 ? 0d : (double)checksCompleted / checksTotal * 100d;
        return new ProjectProgressReport(
            count,
            inProgress,
            completed,
            notStarted,
            checksCompleted,
            checksTotal,
            checksSkipped,
            percent);
    }
}
