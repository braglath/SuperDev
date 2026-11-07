using System.Globalization;
using SuperDev.Services.Activity;

namespace SuperDev.Tests;

public class DateRangeFilterTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static readonly TimeZoneInfo Behind = TimeZoneInfo.CreateCustomTimeZone(
        "TestMinus10",
        TimeSpan.FromHours(-10),
        "TestMinus10",
        "TestMinus10");

    private static readonly TimeZoneInfo Ahead = TimeZoneInfo.CreateCustomTimeZone(
        "TestPlus530",
        new TimeSpan(5, 30, 0),
        "TestPlus530",
        "TestPlus530");

    [Fact]
    public void This_week_uses_the_locale_first_day_and_includes_seven_days()
    {
        var saturday = new DateOnly(2026, 11, 7);
        var week = DateRangeFilter.ThisWeek(saturday, DayOfWeek.Monday);

        Assert.Equal(HistoryPeriodKind.ThisWeek, week.Kind);
        Assert.Equal(new DateOnly(2026, 11, 2), week.Start);
        Assert.Equal(new DateOnly(2026, 11, 8), week.End);
        Assert.Equal(TrendGranularity.Daily, week.ChooseGranularity());
    }

    [Fact]
    public void This_week_can_start_on_sunday()
    {
        var saturday = new DateOnly(2026, 11, 7);
        var week = DateRangeFilter.ThisWeek(saturday, DayOfWeek.Sunday);

        Assert.Equal(new DateOnly(2026, 11, 1), week.Start);
        Assert.Equal(new DateOnly(2026, 11, 7), week.End);
    }

    [Fact]
    public void This_month_covers_the_whole_local_month()
    {
        var month = DateRangeFilter.ThisMonth(new DateOnly(2026, 11, 7));

        Assert.Equal(HistoryPeriodKind.ThisMonth, month.Kind);
        Assert.Equal(new DateOnly(2026, 11, 1), month.Start);
        Assert.Equal(new DateOnly(2026, 11, 30), month.End);
        Assert.Equal(TrendGranularity.Daily, month.ChooseGranularity());
    }

    [Fact]
    public void Custom_range_includes_a_single_day_and_rejects_an_end_before_the_start()
    {
        Assert.True(DateRangeFilter.TryCustom(
            new DateOnly(2026, 11, 4),
            new DateOnly(2026, 11, 4),
            out var sameDay,
            out var sameError));
        Assert.Null(sameError);
        Assert.Equal(1, sameDay!.DayCount);

        Assert.False(DateRangeFilter.TryCustom(
            new DateOnly(2026, 11, 10),
            new DateOnly(2026, 11, 9),
            out var invalid,
            out var error));
        Assert.Null(invalid);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Long_custom_ranges_use_weekly_buckets()
    {
        Assert.True(DateRangeFilter.TryCustom(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 12, 15),
            out var range,
            out _));

        Assert.Equal(TrendGranularity.Weekly, range!.ChooseGranularity());
    }

    [Fact]
    public void Utc_bounds_include_the_local_start_and_end()
    {
        var month = DateRangeFilter.ThisMonth(new DateOnly(2026, 11, 7));
        var (start, end) = month.ToUtc(Utc);

        Assert.True(month.ContainsUtc(start, Utc));
        Assert.True(month.ContainsUtc(end.AddTicks(-1), Utc));
        Assert.False(month.ContainsUtc(start.AddTicks(-1), Utc));
        Assert.False(month.ContainsUtc(end, Utc));
    }

    [Fact]
    public void A_negative_offset_keeps_late_local_evening_inside_the_month()
    {
        var month = DateRangeFilter.ThisMonth(new DateOnly(2026, 11, 15));
        var lateNovember = new DateTime(2026, 12, 1, 9, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateOnly(2026, 11, 30), LocalDate(lateNovember, Behind));
        Assert.True(month.ContainsUtc(lateNovember, Behind));
        Assert.False(month.ContainsUtc(new DateTime(2026, 12, 1, 10, 0, 0, DateTimeKind.Utc), Behind));
        Assert.False(month.ContainsUtc(new DateTime(2026, 11, 1, 9, 59, 0, DateTimeKind.Utc), Behind));
    }

    [Fact]
    public void A_positive_offset_keeps_early_local_morning_inside_the_month()
    {
        var month = DateRangeFilter.ThisMonth(new DateOnly(2026, 11, 15));
        var earlyNovember = new DateTime(2026, 10, 31, 18, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateOnly(2026, 11, 1), LocalDate(earlyNovember, Ahead));
        Assert.True(month.ContainsUtc(earlyNovember, Ahead));
        Assert.False(month.ContainsUtc(earlyNovember.AddMinutes(-1), Ahead));
    }

    [Fact]
    public void Filters_stay_until_the_user_changes_them()
    {
        var history = new HistoryViewModel(
            Utc,
            () => new DateOnly(2026, 11, 7),
            () => DayOfWeek.Monday);
        history.SetSearch("login");
        history.SetWorkType(SuperDev.Models.WorkType.Bug);
        history.SelectProject(HistoryProjectScope.Standalone, null);
        var month = history.AppliedRange;

        Assert.False(history.TryUseCustom(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 2)));
        Assert.Equal(month.Start, history.AppliedRange.Start);
        Assert.Equal(month.End, history.AppliedRange.End);
        Assert.Equal("login", history.Search);
        Assert.Equal(SuperDev.Models.WorkType.Bug, history.WorkType);

        history.SelectThisWeek();
        Assert.Equal(HistoryPeriodKind.ThisWeek, history.SelectedPeriod);
        Assert.Equal(new DateOnly(2026, 11, 2), history.AppliedRange.Start);
        Assert.Equal("login", history.Search);
        Assert.Equal(HistoryProjectScope.Standalone, history.ProjectScope);
        Assert.Null(history.GranularityOverride);
    }

    private static DateOnly LocalDate(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));
}
