using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using SuperDev;
using SuperDev.Models;

namespace SuperDev.Services;

public sealed class ScheduledReminder
{
    public const string DeadlineSlot = "deadline";

    public Guid DocumentId { get; set; }

    /// <summary>
    /// Empty for the checklist reminder. "deadline" for the checklist deadline, "item:{id}" for a check.
    /// </summary>
    public string Slot { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Detail { get; set; }

    public string? FilePath { get; set; }

    public DateTime NextOn { get; set; }

    public int EveryMinutes { get; set; }

    public bool Silent { get; set; }

    public static string ItemSlot(Guid itemId) => $"item:{itemId:N}";

    public static bool TryItemId(string? slot, out Guid itemId)
    {
        itemId = Guid.Empty;
        const string prefix = "item:";
        return !string.IsNullOrEmpty(slot) &&
               slot.StartsWith(prefix, StringComparison.Ordinal) &&
               Guid.TryParse(slot[prefix.Length..], out itemId);
    }
}

public sealed class ReminderNotice
{
    public string Key { get; set; } = string.Empty;

    public Guid DocumentId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public DateTime DeliveredOn { get; set; }

    public string? FilePath { get; set; }

    public string Slot { get; set; } = string.Empty;

    public bool Seen { get; set; }
}

/// <summary>
/// Shared reminder schedule and delivered-notification list. A background process and the
/// open app both use it, so reads and writes stay behind one mutex.
/// </summary>
public static class ReminderSchedule
{
    private const int MaxNotices = 10;
    private static readonly Mutex Gate = new(false, "SuperDevReminders");
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void Upsert(ScheduledReminder incoming)
    {
        if (incoming.EveryMinutes < 0)
            incoming.EveryMinutes = 0;
        incoming.NextOn = AsUtc(incoming.NextOn);

        WithGate(() =>
        {
            var store = ReadSchedule();
            var index = store.FindIndex(item => SameSlot(item, incoming.DocumentId, incoming.Slot));
            if (index >= 0)
            {
                var existing = store[index];
                if (existing.EveryMinutes == incoming.EveryMinutes &&
                    AsUtc(existing.NextOn) > incoming.NextOn &&
                    WasDeliveredUnlocked(incoming.DocumentId, incoming.NextOn, incoming.Slot))
                {
                    existing.Title = incoming.Title;
                    existing.FilePath = incoming.FilePath;
                    existing.Detail = incoming.Detail;
                    existing.Silent = incoming.Silent;
                }
                else
                {
                    store[index] = incoming;
                }
            }
            else if (incoming.EveryMinutes <= 0 &&
                     WasDeliveredUnlocked(incoming.DocumentId, incoming.NextOn, incoming.Slot))
            {
                return;
            }
            else
            {
                store.Add(incoming);
            }

            WriteSchedule(store);
        });
    }

    public static void Remove(Guid documentId)
    {
        WithGate(() =>
        {
            var store = ReadSchedule();
            if (store.RemoveAll(item => item.DocumentId == documentId) > 0)
                WriteSchedule(store);
        });
    }

    public static void RemoveSlot(Guid documentId, string slot)
    {
        WithGate(() =>
        {
            var store = ReadSchedule();
            if (store.RemoveAll(item => SameSlot(item, documentId, slot)) > 0)
                WriteSchedule(store);
        });
    }

    public static void Touch(Guid documentId, string slot, string title, string? filePath, bool silent)
    {
        WithGate(() =>
        {
            var store = ReadSchedule();
            var existing = store.Find(item => SameSlot(item, documentId, slot));
            if (existing is null ||
                (existing.Silent == silent &&
                 existing.Title == title &&
                 string.Equals(existing.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            existing.Title = title;
            existing.FilePath = filePath;
            existing.Silent = silent;
            WriteSchedule(store);
        });
    }

    public static void ReplaceAttached(Guid documentId, IReadOnlyList<ScheduledReminder> wanted)
    {
        WithGate(() =>
        {
            var store = ReadSchedule();
            var manual = store.Where(item => item.DocumentId == documentId && string.IsNullOrEmpty(item.Slot)).ToList();
            var others = store.Where(item => item.DocumentId != documentId).ToList();
            var next = new List<ScheduledReminder>(others);
            next.AddRange(manual);
            foreach (var item in wanted)
            {
                item.DocumentId = documentId;
                item.NextOn = AsUtc(item.NextOn);
                if (item.Slot.Length == 0)
                    item.Slot = ScheduledReminder.DeadlineSlot;
                if (item.EveryMinutes <= 0 && WasDeliveredUnlocked(documentId, item.NextOn, item.Slot))
                    continue;
                next.Add(item);
            }

            if (SameAttached(
                    store.Where(item => item.DocumentId == documentId && !string.IsNullOrEmpty(item.Slot)),
                    next.Where(item => item.DocumentId == documentId && !string.IsNullOrEmpty(item.Slot))))
                return;

            WriteSchedule(next);
        });
    }

    public static IReadOnlyList<ScheduledReminder> All()
    {
        List<ScheduledReminder> items = [];
        WithGate(() => items = ReadSchedule().Select(Copy).ToList());
        return items.OrderBy(item => item.NextOn).ToList();
    }

    public static ScheduledReminder? Find(Guid documentId, string slot = "")
    {
        ScheduledReminder? found = null;
        WithGate(() =>
        {
            var match = ReadSchedule().Find(item => SameSlot(item, documentId, slot));
            if (match is null)
                return;

            found = Copy(match);
        });
        return found;
    }

    public static bool WasDelivered(Guid documentId, DateTime occurrenceUtc, string slot = "") =>
        WithGate(() => WasDeliveredUnlocked(documentId, AsUtc(occurrenceUtc), slot));

    public static void DeliverDue()
    {
        List<ScheduledReminder> due = [];
        WithGate(() =>
        {
            var now = DateTime.UtcNow;
            var store = ReadSchedule();
            var keep = new List<ScheduledReminder>();
            var changed = false;

            foreach (var item in store)
            {
                item.NextOn = AsUtc(item.NextOn);
                if (item.NextOn > now)
                {
                    keep.Add(item);
                    continue;
                }

                if (IsCompletedTarget(item))
                {
                    changed = true;
                    continue;
                }

                changed = true;
                due.Add(Copy(item));

                if (item.EveryMinutes > 0)
                {
                    item.NextOn = Advance(item.NextOn, item.EveryMinutes, now);
                    keep.Add(item);
                }
            }

            if (changed)
                WriteSchedule(keep);

            foreach (var item in due)
                AddNoticeUnlocked(item);
        });

        foreach (var item in due)
        {
            var title = string.IsNullOrWhiteSpace(item.Title) ? "Checklist" : item.Title.Trim();
            var detail = string.IsNullOrWhiteSpace(item.Detail)
                ? ReminderText.NoticeDetail(item.EveryMinutes)
                : item.Detail;
            SystemNotification.Show(title, detail, item.Silent, item.DocumentId, item.FilePath);
        }
    }

    public static IReadOnlyList<ReminderNotice> Notices()
    {
        List<ReminderNotice> items = [];
        WithGate(() => items = ReadCappedNotices());
        return items;
    }

    public static int UnseenCount()
    {
        var count = 0;
        WithGate(() =>
        {
            foreach (var item in ReadCappedNotices())
            {
                if (!item.Seen)
                    count++;
            }
        });
        return count;
    }

    public static void DismissDocument(Guid documentId)
    {
        WithGate(() =>
        {
            var items = ReadNotices();
            if (items.RemoveAll(notice => notice.DocumentId == documentId) > 0)
                WriteNotices(items);
        });
    }

    public static void DismissSlot(Guid documentId, string slot)
    {
        if (string.IsNullOrEmpty(slot))
            return;

        WithGate(() =>
        {
            var items = ReadNotices();
            if (items.RemoveAll(notice => notice.DocumentId == documentId && notice.Slot == slot) > 0)
                WriteNotices(items);
        });
    }

    public static void Dismiss(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        WithGate(() =>
        {
            var items = ReadNotices();
            if (items.RemoveAll(notice => notice.Key == key) > 0)
                WriteNotices(items);
        });
    }

    public static void DismissAll()
    {
        WithGate(() => WriteNotices([]));
    }

    public static void MarkAllSeen()
    {
        WithGate(() =>
        {
            var items = ReadNotices();
            var changed = false;
            foreach (var item in items)
            {
                if (item.Seen)
                    continue;

                item.Seen = true;
                changed = true;
            }

            if (changed)
                WriteNotices(items);
        });
    }

    public static DateTime Advance(DateTime fromUtc, int everyMinutes, DateTime nowUtc)
    {
        fromUtc = AsUtc(fromUtc);
        nowUtc = AsUtc(nowUtc);
        if (everyMinutes <= 0 || fromUtc > nowUtc)
            return fromUtc;

        var stepTicks = TimeSpan.FromMinutes(everyMinutes).Ticks;
        var elapsed = nowUtc.Ticks - fromUtc.Ticks;
        var steps = elapsed / stepTicks + 1;
        return fromUtc.AddTicks(stepTicks * steps);
    }

    private static void AddNoticeUnlocked(ScheduledReminder item)
    {
        var key = Key(item.DocumentId, item.NextOn, item.Slot);
        var items = ReadNotices();
        if (items.Exists(notice => notice.Key == key))
            return;

        items.Insert(0, new ReminderNotice
        {
            Key = key,
            DocumentId = item.DocumentId,
            Title = string.IsNullOrWhiteSpace(item.Title) ? "Checklist" : item.Title.Trim(),
            Detail = string.IsNullOrWhiteSpace(item.Detail)
                ? ReminderText.NoticeDetail(item.EveryMinutes)
                : item.Detail,
            DeliveredOn = DateTime.UtcNow,
            FilePath = item.FilePath,
            Slot = item.Slot ?? string.Empty,
            Seen = false
        });

        if (items.Count > MaxNotices)
            items.RemoveRange(MaxNotices, items.Count - MaxNotices);

        WriteNotices(items);
    }

    private static bool IsCompletedTarget(ScheduledReminder item)
    {
        if (string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath))
            return false;

        try
        {
            var json = File.ReadAllText(item.FilePath);
            var document = JsonSerializer.Deserialize<ChecklistDocument>(json, ChecklistFileService.SerializerOptions);
            if (document is null || document.Id != item.DocumentId)
                return false;

            if (document.IsCompleted)
                return true;

            if (!ScheduledReminder.TryItemId(item.Slot, out var itemId))
                return false;

            var check = document.Items?.Find(entry => entry.Id == itemId);
            return check is null || check.IsCompleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool WasDeliveredUnlocked(Guid documentId, DateTime occurrenceUtc, string slot)
    {
        var notices = ReadNotices();
        var key = Key(documentId, occurrenceUtc, slot);
        if (notices.Exists(notice => notice.Key == key))
            return true;

        if (!string.IsNullOrEmpty(slot))
            return false;

        var legacy = $"{documentId:N}|{AsUtc(occurrenceUtc).Ticks / TimeSpan.TicksPerSecond}";
        return notices.Exists(notice => notice.Key == legacy);
    }

    private static string Key(Guid documentId, DateTime occurrenceUtc, string slot)
    {
        var second = AsUtc(occurrenceUtc).Ticks / TimeSpan.TicksPerSecond;
        var name = slot ?? string.Empty;
        return name.Length == 0
            ? $"{documentId:N}|{second}"
            : $"{documentId:N}|{name}|{second}";
    }

    private static bool SameSlot(ScheduledReminder item, Guid documentId, string slot) =>
        item.DocumentId == documentId && string.Equals(item.Slot ?? string.Empty, slot ?? string.Empty, StringComparison.Ordinal);

    private static ScheduledReminder Copy(ScheduledReminder match) => new()
    {
        DocumentId = match.DocumentId,
        Slot = match.Slot ?? string.Empty,
        Title = match.Title,
        Detail = match.Detail,
        FilePath = match.FilePath,
        NextOn = AsUtc(match.NextOn),
        EveryMinutes = match.EveryMinutes,
        Silent = match.Silent
    };

    private static bool SameAttached(IEnumerable<ScheduledReminder> current, IEnumerable<ScheduledReminder> wanted)
    {
        var left = current.OrderBy(item => item.Slot).Select(Signature).ToList();
        var right = wanted.OrderBy(item => item.Slot).Select(Signature).ToList();
        return left.Count == right.Count && left.SequenceEqual(right);
    }

    private static string Signature(ScheduledReminder item) =>
        $"{item.Slot}|{AsUtc(item.NextOn).Ticks}|{item.EveryMinutes}|{item.Silent}|{item.Title}|{item.Detail}|{item.FilePath}";

    private static List<ScheduledReminder> ReadSchedule()
    {
        try
        {
            if (!File.Exists(SchedulePath))
                return [];

            return JsonSerializer.Deserialize<List<ScheduledReminder>>(File.ReadAllText(SchedulePath), Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static List<ReminderNotice> ReadCappedNotices()
    {
        var items = ReadNotices();
        if (items.Count <= MaxNotices)
            return items;

        items.RemoveRange(MaxNotices, items.Count - MaxNotices);
        WriteNotices(items);
        return items;
    }

    private static List<ReminderNotice> ReadNotices()
    {
        try
        {
            if (!File.Exists(NoticePath))
                return [];

            return JsonSerializer.Deserialize<List<ReminderNotice>>(File.ReadAllText(NoticePath), Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static void WriteSchedule(List<ScheduledReminder> items) =>
        Write(SchedulePath, items);

    private static void WriteNotices(List<ReminderNotice> items) =>
        Write(NoticePath, items);

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Folder);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }

    private static void WithGate(Action action) => WithGate(() =>
    {
        action();
        return true;
    });

    private static T WithGate<T>(Func<T> action)
    {
        var entered = false;
        try
        {
            try
            {
                entered = Gate.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                entered = true;
            }

            if (!entered)
                return default!;

            return action();
        }
        finally
        {
            if (entered)
            {
                try
                {
                    Gate.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }
        }
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static string Folder
    {
        get
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Super Dev");
            return folder;
        }
    }

    private static string SchedulePath => Path.Combine(Folder, "reminder-schedule.json");

    private static string NoticePath => Path.Combine(Folder, "reminder-notifications.json");

    internal static string PidPath => Path.Combine(Folder, "reminder-daemon.pid");
}
