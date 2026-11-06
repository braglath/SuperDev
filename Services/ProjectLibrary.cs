using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuperDev.Models;

namespace SuperDev.Services;

public sealed class ProjectLibrary
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _storePath;
    private readonly List<Project> _projects = [];
    private readonly List<TasklistRecord> _tasklists = [];
    private bool _canSave = true;

    public ProjectLibrary(string? storePath = null)
    {
        if (string.IsNullOrWhiteSpace(storePath))
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Super Dev");
            storePath = Path.Combine(folder, "projects.json");
        }

        _storePath = storePath;
        Load();
    }

    public IReadOnlyList<Project> Projects => _projects;

    public IReadOnlyList<TasklistRecord> Tasklists => _tasklists;

    public Project? Find(Guid id) =>
        _projects.FirstOrDefault(project => project.Id == id);

    public TasklistRecord? FindTasklist(Guid documentId) =>
        _tasklists.FirstOrDefault(record => record.DocumentId == documentId);

    public IReadOnlyList<TasklistRecord> ForProject(Guid projectId)
    {
        return _tasklists
            .Where(record => record.ProjectId == projectId)
            .OrderBy(record => record.Order)
            .ThenBy(record => record.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Stores the overview order for tasklists that belong to the project.
    /// Ids that are not members are ignored. Members missing from the list stay at the end.
    /// </summary>
    public void Reorder(Guid projectId, IReadOnlyList<Guid> documentIds)
    {
        var members = _tasklists.Where(record => record.ProjectId == projectId).ToList();
        if (members.Count < 2)
            return;

        var placed = new HashSet<Guid>();
        var index = 0;
        foreach (var documentId in documentIds)
        {
            var record = members.FirstOrDefault(item => item.DocumentId == documentId);
            if (record is null || !placed.Add(documentId))
                continue;

            record.Order = index++;
        }

        foreach (var record in members
                     .Where(item => !placed.Contains(item.DocumentId))
                     .OrderBy(item => item.Order)
                     .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase))
            record.Order = index++;

        Save();
    }

    public Project Create(ProjectDraft draft)
    {
        var now = DateTime.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = CleanName(draft.Name),
            Description = Clean(draft.Description, 2000),
            Platform = Clean(draft.Platform, 80),
            Framework = Clean(draft.Framework, 80),
            Icon = ProjectAppearance.Icon(draft.Icon),
            Color = ProjectAppearance.Color(draft.Color),
            Status = ProjectStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        _projects.Add(project);
        Save();
        return project;
    }

    public Project? Update(Guid id, ProjectDraft draft)
    {
        var project = Find(id);
        if (project is null)
            return null;

        project.Name = CleanName(draft.Name);
        project.Description = Clean(draft.Description, 2000);
        project.Platform = Clean(draft.Platform, 80);
        project.Framework = Clean(draft.Framework, 80);
        project.Icon = ProjectAppearance.Icon(draft.Icon);
        project.Color = ProjectAppearance.Color(draft.Color);
        project.UpdatedAt = DateTime.UtcNow;
        Save();
        return project;
    }

    public void Remember(Project project)
    {
        if (Find(project.Id) is null)
            return;

        Save();
    }

    public Project? SetStatus(Guid id, ProjectStatus status)
    {
        var project = Find(id);
        if (project is null || project.Status == status)
            return project;

        project.Status = status;
        project.UpdatedAt = DateTime.UtcNow;
        Save();
        return project;
    }

    /// <summary>
    /// Removes the project and clears project ids on the index.
    /// Checklist files are not deleted and are not modified here.
    /// </summary>
    public IReadOnlyList<TasklistRecord> Delete(Guid id)
    {
        var released = new List<TasklistRecord>();
        foreach (var record in _tasklists)
        {
            if (record.ProjectId != id)
                continue;

            record.ProjectId = null;
            released.Add(record);
        }

        _projects.RemoveAll(project => project.Id == id);
        Save();
        return released;
    }

    public void ForgetTasklist(Guid documentId)
    {
        if (_tasklists.RemoveAll(record => record.DocumentId == documentId) > 0)
            Save();
    }

    /// <summary>
    /// Refreshes the index from a checklist document. A null <see cref="ChecklistDocument.ProjectId"/>
    /// keeps the tasklist standalone.
    /// </summary>
    public void Note(ChecklistDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        var fullPath = Normalize(path);
        if (fullPath is null)
            return;

        var progress = ChecklistCounts.Measure(document.Items);
        var record = FindTasklist(document.Id);
        if (record is null)
        {
            record = new TasklistRecord { DocumentId = document.Id };
            _tasklists.Add(record);
        }

        var joining = document.ProjectId is Guid && record.ProjectId != document.ProjectId;
        record.Path = fullPath;
        record.Title = string.IsNullOrWhiteSpace(document.Title)
            ? Path.GetFileName(fullPath)
            : document.Title.Trim();
        record.ProjectId = document.ProjectId;
        if (joining && document.ProjectId is Guid projectId)
            record.Order = NextOrder(projectId, record.DocumentId);
        record.IsCompleted = document.IsCompleted;
        record.CompletedChecks = progress.Completed;
        record.TotalChecks = progress.Total;
        record.SkippedChecks = progress.Skipped;
        record.UpdatedOn = document.UpdatedOn;

        _tasklists.RemoveAll(other =>
            other.DocumentId != document.Id && PathsEqual(other.Path, fullPath));
        Save();
    }

    private int NextOrder(Guid projectId, Guid except)
    {
        var peers = _tasklists.Where(record => record.ProjectId == projectId && record.DocumentId != except);
        return peers.Any() ? peers.Max(record => record.Order) + 1 : 0;
    }

    private void Load()
    {
        if (!File.Exists(_storePath))
            return;

        try
        {
            var json = File.ReadAllText(_storePath);
            var store = JsonSerializer.Deserialize<ProjectStoreFile>(json, SerializerOptions);
            if (store is null || store.Version != CurrentVersion)
            {
                _canSave = false;
                return;
            }

            var seenProjects = new HashSet<Guid>();
            foreach (var project in store.Projects ?? [])
            {
                if (project.Id == Guid.Empty || string.IsNullOrWhiteSpace(project.Name))
                    continue;
                if (!seenProjects.Add(project.Id))
                    continue;

                project.Name = project.Name.Trim();
                project.Description ??= string.Empty;
                project.Platform ??= string.Empty;
                project.Framework ??= string.Empty;
                project.Icon = ProjectAppearance.Icon(project.Icon);
                project.Color = ProjectAppearance.Color(project.Color);
                if (!Enum.IsDefined(project.Status))
                    project.Status = ProjectStatus.Active;
                _projects.Add(project);
            }

            var seenDocuments = new HashSet<Guid>();
            foreach (var record in store.Tasklists ?? [])
            {
                var fullPath = Normalize(record.Path);
                if (record.DocumentId == Guid.Empty || fullPath is null)
                    continue;
                if (!seenDocuments.Add(record.DocumentId))
                    continue;

                record.Path = fullPath;
                record.Title = string.IsNullOrWhiteSpace(record.Title)
                    ? Path.GetFileName(fullPath)
                    : record.Title.Trim();
                if (record.ProjectId == Guid.Empty)
                    record.ProjectId = null;
                if (record.CompletedChecks < 0)
                    record.CompletedChecks = 0;
                if (record.TotalChecks < 0)
                    record.TotalChecks = 0;
                if (record.SkippedChecks < 0)
                    record.SkippedChecks = 0;
                _tasklists.Add(record);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _projects.Clear();
            _tasklists.Clear();
            _canSave = false;
        }
    }

    private void Save()
    {
        if (!_canSave)
            return;

        try
        {
            var directory = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var store = new ProjectStoreFile
            {
                Version = CurrentVersion,
                Projects = _projects,
                Tasklists = _tasklists
            };
            var json = JsonSerializer.Serialize(store, SerializerOptions);
            var temporary = _storePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _storePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string CleanName(string? name)
    {
        var cleaned = Clean(name, 80);
        if (cleaned.Length == 0)
            throw new ArgumentException("A project needs a name.", nameof(name));

        return cleaned;
    }

    private static string Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
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

    private static bool PathsEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private sealed class ProjectStoreFile
    {
        public int Version { get; set; } = CurrentVersion;

        public List<Project>? Projects { get; set; }

        public List<TasklistRecord>? Tasklists { get; set; }
    }
}
