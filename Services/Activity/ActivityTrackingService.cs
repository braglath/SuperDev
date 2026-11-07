using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SuperDev.Models;

namespace SuperDev.Services.Activity;

public sealed class TasklistSnapshot
{
    public Guid TasklistId { get; init; }

    public Guid? ProjectId { get; init; }

    public string Title { get; init; } = string.Empty;

    public WorkType WorkType { get; init; }

    public string? Framework { get; init; }

    public string? FilePath { get; init; }

    public TasklistActivityStatus Status { get; init; }

    public int PendingChecks { get; init; }

    public int CompletedChecks { get; init; }

    public DateTime FirstSeenAt { get; init; }

    public DateTime? DeletedAt { get; init; }
}

/// <summary>
/// Records checklist transitions. The first time an existing file is seen, its current
/// checks are stored as a baseline and no historical events are invented.
/// Opening a document does not start it. A repeated save of the same state writes nothing.
/// </summary>
public sealed class ActivityTrackingService
{
    private readonly ActivityDatabase _database;
    private readonly TimeZoneInfo _zone;
    private readonly Func<DateTime> _utcNow;

    public ActivityTrackingService(
        ActivityDatabase database,
        TimeZoneInfo? zone = null,
        Func<DateTime>? utcNow = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _zone = zone ?? TimeZoneInfo.Local;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public static TasklistActivityStatus DeriveStatus(ChecklistDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.IsCompleted)
            return TasklistActivityStatus.Completed;

        foreach (var item in document.Items)
        {
            if (item.IsCompleted && !item.IsSkipped)
                return TasklistActivityStatus.InProgress;
        }

        return TasklistActivityStatus.Pending;
    }

    /// <summary>
    /// A tasklist created in the app. Already-completed checks on a copy are the starting
    /// state, not new completion events.
    /// </summary>
    public ActivityEvent? RecordCreated(
        ChecklistDocument document,
        string? filePath = null,
        DateTime? timestampUtc = null,
        string? projectName = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Id == Guid.Empty)
            throw new ArgumentException("A tasklist needs an id.", nameof(document));

        var timestamp = Resolve(timestampUtc);
        ActivityEvent? created = null;
        _database.Write((connection, transaction) =>
        {
            if (ReadSnapshot(connection, transaction, document.Id) is not null)
                return;

            created = BuildEvent(document, ActivityEventType.TasklistCreated, timestamp, itemId: null, projectName);
            InsertEvent(connection, transaction, created);
            InsertSnapshot(connection, transaction, document, filePath, timestamp, deletedAt: null, seenAt: timestamp);
            ReplaceChecks(connection, transaction, document);
        });
        return created;
    }

    /// <summary>
    /// Adopts a file that already existed. No events are written, including when checks are already complete.
    /// A later save only records transitions away from this baseline.
    /// </summary>
    public void ObserveExisting(ChecklistDocument document, string? filePath = null, DateTime? observedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Id == Guid.Empty)
            return;

        var observedAt = Resolve(observedAtUtc);
        _database.Write((connection, transaction) =>
        {
            var snapshot = ReadSnapshot(connection, transaction, document.Id);
            if (snapshot is null)
            {
                InsertSnapshot(connection, transaction, document, filePath, observedAt, deletedAt: null, seenAt: observedAt);
                ReplaceChecks(connection, transaction, document);
                return;
            }

            if (snapshot.DeletedAt is not null)
                return;

            // The file is the current truth. Differences were not witnessed, so they do not become events.
            ReplaceChecks(connection, transaction, document);
            UpdateDetails(
                connection,
                transaction,
                document,
                filePath,
                DeriveStatus(document),
                CountPending(document),
                CountCompleted(document),
                observedAt);
        });
    }

    public ActivityEvent? RecordDeleted(Guid tasklistId, Guid? projectId, DateTime? timestampUtc = null)
    {
        if (tasklistId == Guid.Empty)
            return null;

        var timestamp = Resolve(timestampUtc);
        ActivityEvent? deleted = null;
        _database.Write((connection, transaction) =>
        {
            var snapshot = ReadSnapshot(connection, transaction, tasklistId);
            if (snapshot?.DeletedAt is not null)
                return;

            deleted = new ActivityEvent
            {
                TasklistId = tasklistId,
                ProjectId = projectId ?? snapshot?.ProjectId,
                EventType = ActivityEventType.TasklistDeleted,
                EventTimestamp = timestamp
            };
            InsertEvent(connection, transaction, deleted);
            if (snapshot is null)
            {
                InsertDeletedPlaceholder(connection, transaction, tasklistId, projectId, timestamp);
                return;
            }

            using var command = Sql(connection, transaction, """
                UPDATE TasklistSnapshots
                SET DeletedAt = $deleted, UpdatedAt = $deleted
                WHERE TasklistId = $id AND DeletedAt IS NULL
                """);
            command.Parameters.AddWithValue("$deleted", ActivityTime.FormatUtc(timestamp));
            command.Parameters.AddWithValue("$id", Id(tasklistId));
            command.ExecuteNonQuery();
        });
        return deleted;
    }

    /// <summary>
    /// Diffs <paramref name="document"/> against the last stored baseline.
    /// The first capture of an unknown tasklist only stores that baseline.
    /// </summary>
    public IReadOnlyList<ActivityEvent> Capture(
        ChecklistDocument document,
        string? filePath = null,
        DateTime? timestampUtc = null,
        string? projectName = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Id == Guid.Empty)
            return [];

        var timestamp = Resolve(timestampUtc);
        var written = new List<ActivityEvent>();
        _database.Write((connection, transaction) =>
        {
            var snapshot = ReadSnapshot(connection, transaction, document.Id);
            if (snapshot is null || snapshot.DeletedAt is not null)
            {
                if (snapshot is null)
                {
                    InsertSnapshot(connection, transaction, document, filePath, timestamp, deletedAt: null, seenAt: timestamp);
                    ReplaceChecks(connection, transaction, document);
                }

                return;
            }

            var previous = ReadChecks(connection, transaction, document.Id);
            var nextStatus = DeriveStatus(document);
            var pending = CountPending(document);
            var completed = CountCompleted(document);
            var events = new List<ActivityEvent>();

            foreach (var item in document.Items)
            {
                var done = IsDone(item);
                if (!previous.TryGetValue(item.Id, out var wasDone))
                {
                    if (done)
                    {
                        events.Add(BuildEvent(
                            document,
                            ActivityEventType.CheckCompleted,
                            timestamp,
                            item.Id,
                            projectName: null));
                    }

                    continue;
                }

                if (!wasDone && done)
                {
                    events.Add(BuildEvent(
                        document,
                        ActivityEventType.CheckCompleted,
                        timestamp,
                        item.Id,
                        projectName: null));
                }
                else if (wasDone && !done)
                {
                    events.Add(BuildEvent(
                        document,
                        ActivityEventType.CheckReopened,
                        timestamp,
                        item.Id,
                        projectName: null));
                }
            }

            if (snapshot.Status == TasklistActivityStatus.Pending && nextStatus != TasklistActivityStatus.Pending)
            {
                events.Add(BuildEvent(document, ActivityEventType.TasklistStarted, timestamp, null, projectName));
            }

            if (snapshot.Status != TasklistActivityStatus.Completed && nextStatus == TasklistActivityStatus.Completed)
            {
                events.Add(BuildEvent(document, ActivityEventType.TasklistCompleted, timestamp, null, projectName));
            }
            else if (snapshot.Status == TasklistActivityStatus.Completed && nextStatus != TasklistActivityStatus.Completed)
            {
                events.Add(BuildEvent(document, ActivityEventType.TasklistReopened, timestamp, null, projectName));
            }

            var detailsChanged = snapshot.ProjectId != document.ProjectId
                || !string.Equals(snapshot.Title, CleanTitle(document.Title), StringComparison.Ordinal)
                || snapshot.WorkType != document.WorkType
                || !string.Equals(snapshot.Framework, CleanFramework(document.AiFramework), StringComparison.Ordinal)
                || !string.Equals(snapshot.FilePath, CleanPath(filePath), StringComparison.OrdinalIgnoreCase);

            if (events.Count == 0 && !detailsChanged && snapshot.Status == nextStatus
                && snapshot.PendingChecks == pending && snapshot.CompletedChecks == completed
                && SameChecks(previous, document))
            {
                return;
            }

            foreach (var activity in events)
                InsertEvent(connection, transaction, activity);

            ReplaceChecks(connection, transaction, document);
            UpdateDetails(connection, transaction, document, filePath, nextStatus, pending, completed, timestamp);
            written.AddRange(events);
        });
        return written;
    }

    public IReadOnlyList<ActivityEvent> EventsFor(Guid tasklistId)
    {
        return _database.Read(connection =>
        {
            using var command = Sql(connection, null, """
                SELECT Id, TasklistId, ProjectId, ChecklistItemId, EventType, EventTimestamp, Metadata
                FROM ActivityEvents
                WHERE TasklistId = $id
                ORDER BY EventTimestamp, EventType
                """);
            command.Parameters.AddWithValue("$id", Id(tasklistId));
            using var reader = command.ExecuteReader();
            var events = new List<ActivityEvent>();
            while (reader.Read())
                events.Add(ReadEvent(reader));
            return (IReadOnlyList<ActivityEvent>)events;
        });
    }

    public TasklistSnapshot? Snapshot(Guid tasklistId)
    {
        return _database.Read(connection => ReadSnapshot(connection, null, tasklistId));
    }

    private DateTime Resolve(DateTime? timestampUtc) => ActivityTime.AsUtc(timestampUtc ?? _utcNow());

    private ActivityEvent BuildEvent(
        ChecklistDocument document,
        ActivityEventType type,
        DateTime timestamp,
        Guid? itemId,
        string? projectName) =>
        new()
        {
            TasklistId = document.Id,
            ProjectId = document.ProjectId,
            ChecklistItemId = itemId,
            EventType = type,
            EventTimestamp = timestamp,
            Metadata = itemId is null ? Metadata(document, projectName) : null
        };

    private void InsertEvent(SqliteConnection connection, SqliteTransaction transaction, ActivityEvent activity)
    {
        using var command = Sql(connection, transaction, """
            INSERT INTO ActivityEvents
                (Id, TasklistId, ProjectId, ChecklistItemId, EventType, EventTimestamp, LocalDate, Metadata)
            VALUES
                ($id, $tasklist, $project, $item, $type, $timestamp, $localDate, $metadata)
            """);
        command.Parameters.AddWithValue("$id", Id(activity.Id));
        command.Parameters.AddWithValue("$tasklist", Id(activity.TasklistId));
        command.Parameters.AddWithValue("$project", (object?)Id(activity.ProjectId) ?? DBNull.Value);
        command.Parameters.AddWithValue("$item", (object?)Id(activity.ChecklistItemId) ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", activity.EventType.ToString());
        command.Parameters.AddWithValue("$timestamp", ActivityTime.FormatUtc(activity.EventTimestamp));
        command.Parameters.AddWithValue("$localDate", ActivityTime.LocalDate(activity.EventTimestamp, _zone));
        command.Parameters.AddWithValue("$metadata", (object?)activity.Metadata ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void InsertSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChecklistDocument document,
        string? filePath,
        DateTime updatedAt,
        DateTime? deletedAt,
        DateTime seenAt)
    {
        using var command = Sql(connection, transaction, """
            INSERT INTO TasklistSnapshots
                (TasklistId, ProjectId, Title, WorkType, Framework, FilePath, Status,
                 PendingChecks, CompletedChecks, FirstSeenAt, DeletedAt, UpdatedAt)
            VALUES
                ($id, $project, $title, $workType, $framework, $path, $status,
                 $pending, $completed, $seen, $deleted, $updated)
            """);
        BindSnapshot(command, document, filePath, DeriveStatus(document), updatedAt, deletedAt, seenAt);
        command.ExecuteNonQuery();
    }

    private static void InsertDeletedPlaceholder(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid tasklistId,
        Guid? projectId,
        DateTime timestamp)
    {
        using var command = Sql(connection, transaction, """
            INSERT INTO TasklistSnapshots
                (TasklistId, ProjectId, Title, WorkType, Framework, FilePath, Status,
                 PendingChecks, CompletedChecks, FirstSeenAt, DeletedAt, UpdatedAt)
            VALUES
                ($id, $project, $title, $workType, NULL, NULL, $status, 0, 0, $seen, $deleted, $updated)
            """);
        var text = ActivityTime.FormatUtc(timestamp);
        command.Parameters.AddWithValue("$id", Id(tasklistId));
        command.Parameters.AddWithValue("$project", (object?)Id(projectId) ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", "Untitled");
        command.Parameters.AddWithValue("$workType", WorkType.Custom.ToString());
        command.Parameters.AddWithValue("$status", TasklistActivityStatus.Pending.ToString());
        command.Parameters.AddWithValue("$seen", text);
        command.Parameters.AddWithValue("$deleted", text);
        command.Parameters.AddWithValue("$updated", text);
        command.ExecuteNonQuery();
    }

    private static void UpdateDetails(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChecklistDocument document,
        string? filePath,
        TasklistActivityStatus status,
        int pending,
        int completed,
        DateTime updatedAt)
    {
        using var command = Sql(connection, transaction, """
            UPDATE TasklistSnapshots
            SET ProjectId = $project,
                Title = $title,
                WorkType = $workType,
                Framework = $framework,
                FilePath = $path,
                Status = $status,
                PendingChecks = $pending,
                CompletedChecks = $completed,
                UpdatedAt = $updated
            WHERE TasklistId = $id
            """);
        command.Parameters.AddWithValue("$project", (object?)Id(document.ProjectId) ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", CleanTitle(document.Title));
        command.Parameters.AddWithValue("$workType", document.WorkType.ToString());
        command.Parameters.AddWithValue("$framework", (object?)CleanFramework(document.AiFramework) ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", (object?)CleanPath(filePath) ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$pending", pending);
        command.Parameters.AddWithValue("$completed", completed);
        command.Parameters.AddWithValue("$updated", ActivityTime.FormatUtc(updatedAt));
        command.Parameters.AddWithValue("$id", Id(document.Id));
        command.ExecuteNonQuery();
    }

    private static void BindSnapshot(
        SqliteCommand command,
        ChecklistDocument document,
        string? filePath,
        TasklistActivityStatus status,
        DateTime updatedAt,
        DateTime? deletedAt,
        DateTime seenAt)
    {
        command.Parameters.AddWithValue("$id", Id(document.Id));
        command.Parameters.AddWithValue("$project", (object?)Id(document.ProjectId) ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", CleanTitle(document.Title));
        command.Parameters.AddWithValue("$workType", document.WorkType.ToString());
        command.Parameters.AddWithValue("$framework", (object?)CleanFramework(document.AiFramework) ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", (object?)CleanPath(filePath) ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$pending", CountPending(document));
        command.Parameters.AddWithValue("$completed", CountCompleted(document));
        command.Parameters.AddWithValue("$seen", ActivityTime.FormatUtc(seenAt));
        command.Parameters.AddWithValue("$deleted", deletedAt is DateTime deleted ? ActivityTime.FormatUtc(deleted) : DBNull.Value);
        command.Parameters.AddWithValue("$updated", ActivityTime.FormatUtc(updatedAt));
    }

    private static void ReplaceChecks(SqliteConnection connection, SqliteTransaction transaction, ChecklistDocument document)
    {
        using (var delete = Sql(connection, transaction, "DELETE FROM CheckSnapshots WHERE TasklistId = $id"))
        {
            delete.Parameters.AddWithValue("$id", Id(document.Id));
            delete.ExecuteNonQuery();
        }

        foreach (var item in document.Items)
        {
            if (item.Id == Guid.Empty)
                continue;

            using var insert = Sql(connection, transaction, """
                INSERT INTO CheckSnapshots (TasklistId, ChecklistItemId, IsCompleted)
                VALUES ($tasklist, $item, $done)
                """);
            insert.Parameters.AddWithValue("$tasklist", Id(document.Id));
            insert.Parameters.AddWithValue("$item", Id(item.Id));
            insert.Parameters.AddWithValue("$done", IsDone(item) ? 1 : 0);
            insert.ExecuteNonQuery();
        }
    }

    private static Dictionary<Guid, bool> ReadChecks(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid tasklistId)
    {
        using var command = Sql(connection, transaction, """
            SELECT ChecklistItemId, IsCompleted
            FROM CheckSnapshots
            WHERE TasklistId = $id
            """);
        command.Parameters.AddWithValue("$id", Id(tasklistId));
        using var reader = command.ExecuteReader();
        var checks = new Dictionary<Guid, bool>();
        while (reader.Read())
            checks[Guid.Parse(reader.GetString(0))] = reader.GetInt32(1) != 0;
        return checks;
    }

    private static bool SameChecks(Dictionary<Guid, bool> previous, ChecklistDocument document)
    {
        var seen = 0;
        foreach (var item in document.Items)
        {
            if (item.Id == Guid.Empty)
                continue;

            seen++;
            if (!previous.TryGetValue(item.Id, out var wasDone) || wasDone != IsDone(item))
                return false;
        }

        return seen == previous.Count;
    }

    private static TasklistSnapshot? ReadSnapshot(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid tasklistId)
    {
        using var command = Sql(connection, transaction, """
            SELECT TasklistId, ProjectId, Title, WorkType, Framework, FilePath, Status,
                   PendingChecks, CompletedChecks, FirstSeenAt, DeletedAt
            FROM TasklistSnapshots
            WHERE TasklistId = $id
            """);
        command.Parameters.AddWithValue("$id", Id(tasklistId));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new TasklistSnapshot
        {
            TasklistId = Guid.Parse(reader.GetString(0)),
            ProjectId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
            Title = reader.GetString(2),
            WorkType = ParseWorkType(reader.GetString(3)),
            Framework = reader.IsDBNull(4) ? null : reader.GetString(4),
            FilePath = reader.IsDBNull(5) ? null : reader.GetString(5),
            Status = Enum.Parse<TasklistActivityStatus>(reader.GetString(6)),
            PendingChecks = reader.GetInt32(7),
            CompletedChecks = reader.GetInt32(8),
            FirstSeenAt = ActivityTime.ParseUtc(reader.GetString(9)),
            DeletedAt = reader.IsDBNull(10) ? null : ActivityTime.ParseUtc(reader.GetString(10))
        };
    }

    private static ActivityEvent ReadEvent(SqliteDataReader reader) =>
        new()
        {
            Id = Guid.Parse(reader.GetString(0)),
            TasklistId = Guid.Parse(reader.GetString(1)),
            ProjectId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            ChecklistItemId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
            EventType = Enum.Parse<ActivityEventType>(reader.GetString(4)),
            EventTimestamp = ActivityTime.ParseUtc(reader.GetString(5)),
            Metadata = reader.IsDBNull(6) ? null : reader.GetString(6)
        };

    private static string? Metadata(ChecklistDocument document, string? projectName)
    {
        var payload = new Dictionary<string, string?>
        {
            ["title"] = CleanTitle(document.Title),
            ["workType"] = document.WorkType.ToString(),
            ["framework"] = CleanFramework(document.AiFramework),
            ["projectName"] = string.IsNullOrWhiteSpace(projectName) ? null : projectName.Trim()
        };
        return JsonSerializer.Serialize(payload, MetadataJson);
    }

    private static readonly JsonSerializerOptions MetadataJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static int CountPending(ChecklistDocument document)
    {
        var count = 0;
        foreach (var item in document.Items)
        {
            if (!item.IsCompleted && !item.IsSkipped)
                count++;
        }

        return count;
    }

    internal static int CountCompleted(ChecklistDocument document)
    {
        var count = 0;
        foreach (var item in document.Items)
        {
            if (IsDone(item))
                count++;
        }

        return count;
    }

    private static bool IsDone(ChecklistItem item) => item.IsCompleted && !item.IsSkipped;

    private static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "Untitled";

        var trimmed = title.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }

    private static string? CleanFramework(string? framework)
    {
        if (string.IsNullOrWhiteSpace(framework))
            return null;

        var trimmed = framework.Trim();
        return trimmed.Length <= 80 ? trimmed : trimmed[..80];
    }

    private static string? CleanPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }

    private static WorkType ParseWorkType(string value) =>
        Enum.TryParse<WorkType>(value, out var workType) ? workType : WorkType.Custom;

    private static string? Id(Guid? value) => value is Guid id && id != Guid.Empty ? id.ToString("D") : null;

    private static string Id(Guid value) => value.ToString("D");

    private static SqliteCommand Sql(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
