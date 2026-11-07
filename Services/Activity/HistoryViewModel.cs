using System;
using System.Collections.Generic;
using System.Globalization;
using SuperDev.Models;

namespace SuperDev.Services.Activity;

public sealed class HistoryProjectOption
{
    public HistoryProjectScope Scope { get; init; }

    public Guid? ProjectId { get; init; }

    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// Session state for the History page. Leaving the page does not clear it.
/// </summary>
public sealed class HistoryViewModel
{
    public const int AllCompletedLimit = 200;

    private readonly TimeZoneInfo _zone;
    private readonly Func<DateOnly> _today;
    private readonly Func<DayOfWeek> _firstDay;

    public HistoryViewModel(
        TimeZoneInfo? zone = null,
        Func<DateOnly>? today = null,
        Func<DayOfWeek>? firstDayOfWeek = null)
    {
        _zone = zone ?? TimeZoneInfo.Local;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        _firstDay = firstDayOfWeek ?? (() => CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);
        SelectThisMonth();
    }

    public HistoryPeriodKind SelectedPeriod { get; private set; }

    public DateRangeFilter AppliedRange { get; private set; } = null!;

    public DateOnly? CustomStart { get; private set; }

    public DateOnly? CustomEnd { get; private set; }

    public string? RangeError { get; private set; }

    public HistoryProjectScope ProjectScope { get; private set; } = HistoryProjectScope.All;

    public Guid? ProjectId { get; private set; }

    public string Search { get; private set; } = string.Empty;

    public WorkType? WorkType { get; private set; }

    public TrendGranularity? GranularityOverride { get; private set; }

    public bool ShowingAllCompleted { get; private set; }

    public TimeZoneInfo Zone => _zone;

    public void SelectThisWeek()
    {
        SelectedPeriod = HistoryPeriodKind.ThisWeek;
        AppliedRange = DateRangeFilter.ThisWeek(_today(), _firstDay());
        RangeError = null;
        GranularityOverride = null;
        ShowingAllCompleted = false;
    }

    public void SelectThisMonth()
    {
        SelectedPeriod = HistoryPeriodKind.ThisMonth;
        AppliedRange = DateRangeFilter.ThisMonth(_today());
        RangeError = null;
        GranularityOverride = null;
        ShowingAllCompleted = false;
    }

    public void BeginCustom()
    {
        SelectedPeriod = HistoryPeriodKind.Custom;
        var today = _today();
        CustomStart ??= new DateOnly(today.Year, today.Month, 1);
        CustomEnd ??= today;
        TryUseCustom(CustomStart.Value, CustomEnd.Value);
    }

    public bool TryUseCustom(DateOnly start, DateOnly end)
    {
        SelectedPeriod = HistoryPeriodKind.Custom;
        CustomStart = start;
        CustomEnd = end;
        if (!DateRangeFilter.TryCustom(start, end, out var range, out var error) || range is null)
        {
            RangeError = error;
            return false;
        }

        AppliedRange = range;
        RangeError = null;
        GranularityOverride = null;
        ShowingAllCompleted = false;
        return true;
    }

    public void SelectProject(HistoryProjectScope scope, Guid? projectId)
    {
        if (scope == HistoryProjectScope.Project && projectId is null)
            scope = HistoryProjectScope.All;

        ProjectScope = scope;
        ProjectId = scope == HistoryProjectScope.Project ? projectId : null;
        ShowingAllCompleted = false;
    }

    public void SetSearch(string? search)
    {
        Search = search?.Trim() ?? string.Empty;
        ShowingAllCompleted = false;
    }

    public void SetWorkType(WorkType? workType)
    {
        WorkType = workType;
        ShowingAllCompleted = false;
    }

    public void SelectGranularity(TrendGranularity granularity) =>
        GranularityOverride = granularity;

    public void SetShowingAll(bool showingAll) =>
        ShowingAllCompleted = showingAll;

    public HistoryQuery CreateQuery(IReadOnlyDictionary<Guid, string>? projectNames) =>
        new()
        {
            Range = AppliedRange,
            Zone = _zone,
            ProjectScope = ProjectScope,
            ProjectId = ProjectId,
            TitleSearch = string.IsNullOrWhiteSpace(Search) ? null : Search,
            WorkType = WorkType,
            Granularity = GranularityOverride,
            CompletedLimit = ShowingAllCompleted ? AllCompletedLimit : HistoryStatisticsService.PreviewLimit,
            ProjectNames = projectNames
        };
}

public static class WorkTypeText
{
    public static string Label(WorkType workType) =>
        workType switch
        {
            WorkType.Bug => "Bug Fix",
            WorkType.Feature => "Feature",
            WorkType.Development => "Development",
            WorkType.Testing => "Testing",
            WorkType.PullRequest => "Pull Request",
            WorkType.Blank => "Blank",
            WorkType.Daily => "Daily",
            WorkType.Custom => "Custom",
            _ => workType.ToString()
        };
}
