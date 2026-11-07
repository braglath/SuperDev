using System;
using System.Globalization;

namespace SuperDev.Services.Activity;

internal static class ActivityTime
{
    public const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffff";

    public const string DateFormat = "yyyy-MM-dd";

    public static DateTime AsUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    public static string FormatUtc(DateTime value) =>
        AsUtc(value).ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public static DateTime ParseUtc(string text) =>
        DateTime.SpecifyKind(
            DateTime.ParseExact(text, TimestampFormat, CultureInfo.InvariantCulture),
            DateTimeKind.Utc);

    public static string LocalDate(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), zone);
        return DateOnly.FromDateTime(local).ToString(DateFormat, CultureInfo.InvariantCulture);
    }

    public static DateOnly ParseDate(string text) =>
        DateOnly.ParseExact(text, DateFormat, CultureInfo.InvariantCulture);
}
