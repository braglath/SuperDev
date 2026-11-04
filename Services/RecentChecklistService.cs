using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuperDev.Models;

namespace SuperDev.Services;

public sealed class RecentChecklistService
{
    public const int MaxEntries = 6;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _storePath;
    private readonly List<RecentChecklistEntry> _entries = [];

    public RecentChecklistService(string? storePath = null)
    {
        if (string.IsNullOrWhiteSpace(storePath))
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Super Dev");
            storePath = Path.Combine(folder, "recent-checklists.json");
        }

        _storePath = storePath;
        Load();
    }

    public IReadOnlyList<RecentChecklistEntry> Entries => _entries;

    public RecentChecklistEntry? Find(string path) =>
        _entries.FirstOrDefault(entry => PathsEqual(entry.Path, path));

    public void Remember(string path, string title, WorkType workType, bool isCompleted)
    {
        var fullPath = Normalize(path);
        if (fullPath is null)
            return;

        _entries.RemoveAll(entry => PathsEqual(entry.Path, fullPath));
        _entries.Insert(0, new RecentChecklistEntry
        {
            Path = fullPath,
            Title = string.IsNullOrWhiteSpace(title)
                ? Path.GetFileName(fullPath)
                : title.Trim(),
            WorkType = Enum.IsDefined(workType) ? workType : WorkType.Blank,
            IsCompleted = isCompleted,
            LastUsedOn = DateTime.UtcNow
        });

        if (_entries.Count > MaxEntries)
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);

        Save();
    }

    public void Remove(string path)
    {
        if (_entries.RemoveAll(entry => PathsEqual(entry.Path, path)) > 0)
            Save();
    }

    private void Load()
    {
        if (!File.Exists(_storePath))
            return;

        try
        {
            var json = File.ReadAllText(_storePath);
            var store = JsonSerializer.Deserialize<RecentChecklistStore>(json, SerializerOptions);
            if (store is null || store.Version != RecentChecklistStore.CurrentVersion || store.Items is null)
                return;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in store.Items.OrderByDescending(item => item.LastUsedOn))
            {
                var fullPath = Normalize(entry.Path);
                if (fullPath is null || string.IsNullOrWhiteSpace(entry.Title))
                    continue;

                if (!seen.Add(fullPath))
                    continue;

                entry.Path = fullPath;
                if (!Enum.IsDefined(entry.WorkType))
                    entry.WorkType = WorkType.Blank;

                _entries.Add(entry);
                if (_entries.Count == MaxEntries)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _entries.Clear();
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var store = new RecentChecklistStore { Items = _entries };
            var json = JsonSerializer.Serialize(store, SerializerOptions);
            var temporaryPath = _storePath + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, _storePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The checklist file itself was already saved. A recent-list
            // write failure should not block that.
        }
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        var fullLeft = Normalize(left);
        var fullRight = Normalize(right);
        return fullLeft is not null &&
               fullRight is not null &&
               string.Equals(fullLeft, fullRight, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecentChecklistStore
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        public List<RecentChecklistEntry> Items { get; set; } = [];
    }
}
