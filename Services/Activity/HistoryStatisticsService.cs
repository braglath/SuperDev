using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SuperDev.Models;

namespace SuperDev.Services.Activity;

/// <summary>
/// Counting rules:
/// Period events use the event timestamp, inclusive of the local start and end dates.
/// Completed tasklists are distinct tasklists with a TasklistCompleted event in the range.
/// Started tasklists are distinct tasklists whose earliest TasklistStarted falls in the range.
/// Checks completed and reopened count each real transition, not each save.
/// The trend counts CheckCompleted events. Days or weeks with none stay at zero. Nothing is estimated.
/// In progress, pending, pending checks, and the completion rate use current snapshot state
/// for tasklists that were active during the range.
/// A tasklist is active when it was first seen before the range ended, is not deleted,
/// and was not already finished before the range. Finished before the range means the latest
/// TasklistCompleted before the range end is earlier than the range start and no later reopen
/// happened before the range end. A tasklist that was already complete when first seen, with
/// no completion event, and was first seen before the range, is also already finished.
/// First-seen is when tracking observed the tasklist. File creation dates are not backfilled.
/// Current project membership filters current-state counts. Event project ids filter period counts.
/// Title and work type filter the completed list only.
/// </summary>
public sealed class HistoryStatisticsService
{
    public const int PreviewLimit = 4;

    private readonly ActivityDatabase _database;

    public HistoryStatisticsService(ActivityDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public HistoryDashboard Query(HistoryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Range);
        ArgumentNullException.ThrowIfNull(query.Zone);

        var (startUtc, endUtc) = query.Range.ToUtc(query.Zone);
        var startText = ActivityTime.FormatUtc(startUtc);
        var endText = ActivityTime.FormatUtc(endUtc);
        var granularity = query.Granularity ?? query.Range.ChooseGranularity();
        var projectNames = query.ProjectNames ?? new Dictionary<Guid, string>();

        return _database.Read(connection =>
        {
            var completedTasklists = CountDistinct(
                connection,
                ActivityEventType.TasklistCompleted,
                startText,
                endText,
                query);
            var startedTasklists = CountFirstStarts(connection, startText, endText, query);
            var checksCompleted = CountEvents(connection, ActivityEventType.CheckCompleted, startText, endText, query);
            var checksReopened = CountEvents(connection, ActivityEventType.CheckReopened, startText, endText, query);
            var daily = DailyCompletions(connection, startText, endText, query);
            var active = ActiveSnapshots(connection, startText, endText, query);

            var inProgress = 0;
            var pendingTasklists = 0;
            var pendingChecks = 0;
            var completedChecks = 0;
            foreach (var snapshot in active)
            {
                pendingChecks += snapshot.PendingChecks;
                completedChecks += snapshot.CompletedChecks;
                if (snapshot.Status == TasklistActivityStatus.InProgress)
                    inProgress++;
                else if (snapshot.Status == TasklistActivityStatus.Pending)
                    pendingTasklists++;
            }

            var denominator = completedChecks + pendingChecks;
            var rate = denominator == 0
                ? 0
                : (int)Math.Round(100.0 * completedChecks / denominator, MidpointRounding.AwayFromZero);

            var limit = query.CompletedLimit < 0 ? 0 : query.CompletedLimit;
            var matches = CompletedMatches(connection, startText, endText, query, projectNames);
            var shown = limit == 0 || matches.Count <= limit ? matches : matches.GetRange(0, limit);

            return new HistoryDashboard
            {
                Range = query.Range,
                CompletedTasklists = completedTasklists,
                StartedTasklists = startedTasklists,
                InProgressTasklists = inProgress,
                PendingTasklists = pendingTasklists,
                ChecksCompleted = checksCompleted,
                ChecksReopened = checksReopened,
                PendingChecks = pendingChecks,
                CompletionRatePercent = rate,
                Granularity = granularity,
                Trend = BuildTrend(query.Range, granularity, daily),
                Completed = shown,
                CompletedMatchCount = matches.Count
            };
        });
    }

    private static int CountDistinct(
        SqliteConnection connection,
        ActivityEventType type,
        string start,
        string end,
        HistoryQuery query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(DISTINCT TasklistId)
            FROM ActivityEvents
            WHERE EventType = $type
              AND EventTimestamp >= $start
              AND EventTimestamp < $end
              AND {ProjectSql("ProjectId")}
            """;
        BindPeriod(command, type, start, end, query);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int CountEvents(
        SqliteConnection connection,
        ActivityEventType type,
        string start,
        string end,
        HistoryQuery query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM ActivityEvents
            WHERE EventType = $type
              AND EventTimestamp >= $start
              AND EventTimestamp < $end
              AND {ProjectSql("ProjectId")}
            """;
        BindPeriod(command, type, start, end, query);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int CountFirstStarts(SqliteConnection connection, string start, string end, HistoryQuery query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM (
                SELECT TasklistId, MIN(EventTimestamp) AS FirstStart
                FROM ActivityEvents
                WHERE EventType = $type
                GROUP BY TasklistId
            ) first
            WHERE first.FirstStart >= $start
              AND first.FirstStart < $end
              AND EXISTS (
                  SELECT 1
                  FROM ActivityEvents e
                  WHERE e.TasklistId = first.TasklistId
                    AND e.EventType = $type
                    AND e.EventTimestamp = first.FirstStart
                    AND {ProjectSql("e.ProjectId")}
              )
            """;
        BindPeriod(command, ActivityEventType.TasklistStarted, start, end, query);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static Dictionary<DateOnly, int> DailyCompletions(
        SqliteConnection connection,
        string start,
        string end,
        HistoryQuery query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT LocalDate, COUNT(*)
            FROM ActivityEvents
            WHERE EventType = $type
              AND EventTimestamp >= $start
              AND EventTimestamp < $end
              AND {ProjectSql("ProjectId")}
            GROUP BY LocalDate
            """;
        BindPeriod(command, ActivityEventType.CheckCompleted, start, end, query);
        using var reader = command.ExecuteReader();
        var counts = new Dictionary<DateOnly, int>();
        while (reader.Read())
            counts[ActivityTime.ParseDate(reader.GetString(0))] = reader.GetInt32(1);
        return counts;
    }

    private static List<ActiveSnapshot> ActiveSnapshots(
        SqliteConnection connection,
        string start,
        string end,
        HistoryQuery query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.TasklistId, s.Status, s.PendingChecks, s.CompletedChecks, s.FirstSeenAt,
                   l.LastCompleted, l.LastReopened
            FROM TasklistSnapshots s
            LEFT JOIN (
                SELECT TasklistId,
                       MAX(CASE WHEN EventType = 'TasklistCompleted' AND EventTimestamp < $end THEN EventTimestamp END) AS LastCompleted,
                       MAX(CASE WHEN EventType = 'TasklistReopened' AND EventTimestamp < $end THEN EventTimestamp END) AS LastReopened
                FROM ActivityEvents
                WHERE EventType IN ('TasklistCompleted', 'TasklistReopened')
                  AND EventTimestamp < $end
                GROUP BY TasklistId
            ) l ON l.TasklistId = s.TasklistId
            WHERE s.DeletedAt IS NULL
              AND s.FirstSeenAt < $end
              AND {ProjectSql("s.ProjectId")}
            """;
        command.Parameters.AddWithValue("$end", end);
        BindProject(command, query);
        using var reader = command.ExecuteReader();
        var active = new List<ActiveSnapshot>();
        while (reader.Read())
        {
            var firstSeen = reader.GetString(4);
            var lastCompleted = reader.IsDBNull(5) ? null : reader.GetString(5);
            var lastReopened = reader.IsDBNull(6) ? null : reader.GetString(6);
            var status = Enum.Parse<TasklistActivityStatus>(reader.GetString(1));
            if (!WasActive(status, firstSeen, lastCompleted, lastReopened, start))
                continue;

            active.Add(new ActiveSnapshot(
                status,
                reader.GetInt32(2),
                reader.GetInt32(3)));
        }

        return active;
    }

    internal static bool WasActive(
        TasklistActivityStatus status,
        string firstSeen,
        string? lastCompleted,
        string? lastReopened,
        string rangeStart)
    {
        var finishedBefore = lastCompleted is not null
            && string.Compare(lastCompleted, rangeStart, StringComparison.Ordinal) < 0
            && (lastReopened is null || string.Compare(lastReopened, lastCompleted, StringComparison.Ordinal) < 0);
        if (finishedBefore)
            return false;

        var arrivedComplete = status == TasklistActivityStatus.Completed
            && lastCompleted is null
            && string.Compare(firstSeen, rangeStart, StringComparison.Ordinal) < 0
            && (lastReopened is null || string.Compare(lastReopened, rangeStart, StringComparison.Ordinal) < 0);
        return !arrivedComplete;
    }

    private static List<CompletedTasklistEntry> CompletedMatches(
        SqliteConnection connection,
        string start,
        string end,
        HistoryQuery query,
        IReadOnlyDictionary<Guid, string> projectNames)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT e.TasklistId, e.EventTimestamp, e.ProjectId, e.Metadata,
                   s.Title, s.WorkType, s.Framework, s.FilePath, s.ProjectId
            FROM ActivityEvents e
            INNER JOIN (
                SELECT TasklistId, MAX(EventTimestamp) AS CompletedAt
                FROM ActivityEvents
                WHERE EventType = $type
                  AND EventTimestamp >= $start
                  AND EventTimestamp < $end
                  AND {ProjectSql("ProjectId")}
                GROUP BY TasklistId
            ) latest ON latest.TasklistId = e.TasklistId AND latest.CompletedAt = e.EventTimestamp
            LEFT JOIN TasklistSnapshots s ON s.TasklistId = e.TasklistId
            WHERE e.EventType = $type
            ORDER BY e.EventTimestamp DESC, e.TasklistId
            """;
        BindPeriod(command, ActivityEventType.TasklistCompleted, start, end, query);
        using var reader = command.ExecuteReader();
        var matches = new List<CompletedTasklistEntry>();
        var seen = new HashSet<Guid>();
        var search = string.IsNullOrWhiteSpace(query.TitleSearch) ? null : query.TitleSearch.Trim();
        while (reader.Read())
        {
            var id = Guid.Parse(reader.GetString(0));
            if (!seen.Add(id))
                continue;

            var metadata = reader.IsDBNull(3) ? null : ReadMetadata(reader.GetString(3));
            var title = reader.IsDBNull(4) ? metadata?.Title ?? "Untitled" : reader.GetString(4);
            var workType = reader.IsDBNull(5)
                ? ParseWorkType(metadata?.WorkType)
                : ParseWorkType(reader.GetString(5));
            if (query.WorkType is WorkType required && workType != required)
                continue;

            if (search is not null && title.Contains(search, StringComparison.OrdinalIgnoreCase) != true)
                continue;

            var framework = reader.IsDBNull(6) ? metadata?.Framework : reader.GetString(6);
            var snapshotProject = reader.IsDBNull(8) ? (Guid?)null : Guid.Parse(reader.GetString(8));
            var eventProject = reader.IsDBNull(2) ? (Guid?)null : Guid.Parse(reader.GetString(2));
            var projectId = snapshotProject ?? eventProject;
            string? projectName = null;
            if (projectId is Guid named && projectNames.TryGetValue(named, out var current))
                projectName = current;
            else if (!string.IsNullOrWhiteSpace(metadata?.ProjectName))
                projectName = metadata.ProjectName;

            matches.Add(new CompletedTasklistEntry
            {
                TasklistId = id,
                ProjectId = projectId,
                ProjectName = projectName,
                Title = title,
                WorkType = workType,
                Framework = string.IsNullOrWhiteSpace(framework) ? null : framework,
                FilePath = reader.IsDBNull(7) ? null : reader.GetString(7),
                CompletedAtUtc = ActivityTime.ParseUtc(reader.GetString(1))
            });
        }

        return matches;
    }

    private static List<TrendBucket> BuildTrend(
        DateRangeFilter range,
        TrendGranularity granularity,
        Dictionary<DateOnly, int> daily)
    {
        var buckets = new List<TrendBucket>();
        if (granularity == TrendGranularity.Daily)
        {
            for (var day = range.Start; day <= range.End; day = day.AddDays(1))
            {
                daily.TryGetValue(day, out var count);
                buckets.Add(Bucket(day, day, count));
            }

            return buckets;
        }

        for (var day = range.Start; day <= range.End;)
        {
            var bucketEnd = day.AddDays(6);
            if (bucketEnd > range.End)
                bucketEnd = range.End;

            var count = 0;
            for (var cursor = day; cursor <= bucketEnd; cursor = cursor.AddDays(1))
            {
                if (daily.TryGetValue(cursor, out var value))
                    count += value;
            }

            buckets.Add(Bucket(day, bucketEnd, count));
            day = bucketEnd.AddDays(1);
        }

        return buckets;
    }

    private static TrendBucket Bucket(DateOnly start, DateOnly end, int count) =>
        new()
        {
            Start = start,
            End = end,
            Count = count,
            Label = start.ToString("MMM d", CultureInfo.CurrentCulture)
        };

    private static string ProjectSql(string column) =>
        $"""
        (
            $scope = 'all'
            OR ($scope = 'standalone' AND {column} IS NULL)
            OR ($scope = 'project' AND {column} = $projectId)
        )
        """;

    private static void BindPeriod(
        SqliteCommand command,
        ActivityEventType type,
        string start,
        string end,
        HistoryQuery query)
    {
        command.Parameters.AddWithValue("$type", type.ToString());
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);
        BindProject(command, query);
    }

    private static void BindProject(SqliteCommand command, HistoryQuery query)
    {
        command.Parameters.AddWithValue("$scope", query.ProjectScope switch
        {
            HistoryProjectScope.Standalone => "standalone",
            HistoryProjectScope.Project => "project",
            _ => "all"
        });
        command.Parameters.AddWithValue("$projectId", query.ProjectId is Guid id ? id.ToString("D") : "");
    }

    private static WorkType ParseWorkType(string? value) =>
        Enum.TryParse<WorkType>(value, out var workType) ? workType : WorkType.Custom;

    private static EventMetadata? ReadMetadata(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<EventMetadata>(json, MetadataJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ActiveSnapshot(TasklistActivityStatus Status, int PendingChecks, int CompletedChecks);

    private static readonly JsonSerializerOptions MetadataJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class EventMetadata
    {
        public string? Title { get; set; }

        public string? WorkType { get; set; }

        public string? Framework { get; set; }

        public string? ProjectName { get; set; }
    }
}
