using System;
using System.Globalization;

namespace SuperDev.Services.Activity;

public enum HistoryPeriodKind
{
    ThisWeek,
    ThisMonth,
    Custom
}

public enum HistoryProjectScope
{
    All,
    Standalone,
    Project
}

public enum TrendGranularity
{
    Daily,
    Weekly
}

/// <summary>
/// Inclusive local dates. The end date is part of the range. An end before the start is rejected.
/// </summary>
public sealed class DateRangeFilter
{
    private DateRangeFilter(HistoryPeriodKind kind, DateOnly start, DateOnly end)
    {
        Kind = kind;
        Start = start;
        End = end;
    }

    public HistoryPeriodKind Kind { get; }

    public DateOnly Start { get; }

    public DateOnly End { get; }

    public int DayCount => End.DayNumber - Start.DayNumber + 1;

    public string Label(CultureInfo? culture = null)
    {
        var format = culture ?? CultureInfo.CurrentCulture;
        return Start.ToString("MMM d, yyyy", format) + " – " + End.ToString("MMM d, yyyy", format);
    }

    public static DateRangeFilter ThisWeek(DateOnly today, DayOfWeek firstDayOfWeek)
    {
        var delta = ((int)today.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        var start = today.AddDays(-delta);
        return new DateRangeFilter(HistoryPeriodKind.ThisWeek, start, start.AddDays(6));
    }

    public static DateRangeFilter ThisMonth(DateOnly today)
    {
        var start = new DateOnly(today.Year, today.Month, 1);
        return new DateRangeFilter(HistoryPeriodKind.ThisMonth, start, start.AddMonths(1).AddDays(-1));
    }

    public static bool TryCustom(DateOnly start, DateOnly end, out DateRangeFilter? range, out string? error)
    {
        if (end < start)
        {
            range = null;
            error = "The end date can't be earlier than the start date.";
            return false;
        }

        range = new DateRangeFilter(HistoryPeriodKind.Custom, start, end);
        error = null;
        return true;
    }

    /// <summary>
    /// Local start of <see cref="Start"/> inclusive through the local start of the day after <see cref="End"/> exclusive.
    /// </summary>
    public (DateTime StartUtc, DateTime EndExclusiveUtc) ToUtc(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return (ToUtc(Start, zone), ToUtc(End.AddDays(1), zone));
    }

    public bool ContainsUtc(DateTime timestampUtc, TimeZoneInfo zone)
    {
        var (start, end) = ToUtc(zone);
        var utc = ActivityTime.AsUtc(timestampUtc);
        return utc >= start && utc < end;
    }

    /// <summary>
    /// This week is always daily. A month is daily. Longer custom ranges switch to weeks so the bars stay readable.
    /// </summary>
    public TrendGranularity ChooseGranularity()
    {
        if (Kind == HistoryPeriodKind.ThisWeek)
            return TrendGranularity.Daily;

        return DayCount <= 31 ? TrendGranularity.Daily : TrendGranularity.Weekly;
    }

    private static DateTime ToUtc(DateOnly day, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(local, zone), DateTimeKind.Utc);
    }
}
