using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SuperDev.Models;
using SuperDev.Services;

namespace SuperDev.Views;

public partial class MainWindow
{
    private readonly ProjectLibrary _projects = new();
    private readonly List<ProjectTab> _projectTabs = [];
    private ProjectTab? _activeProjectTab;
    private readonly Dictionary<Border, ProjectBarMotion> _projectBars = new();
    private bool _projectBarClock;
    private static readonly TimeSpan ProjectBarDuration = TimeSpan.FromMilliseconds(280);

    private bool HasOpenTabs() => _tabs.Count > 0 || _projectTabs.Count > 0;

    private bool StripNeedsGroups() =>
        _projectTabs.Count > 0 || _tabs.Any(tab => tab.OpenedFromProjectId is not null);

    private Guid? _editingProjectId;
    private HomeSurface _editorReturn = HomeSurface.Dashboard;
    private bool _editorReturnToProject;

    private void CreateProject_Click(object? sender, RoutedEventArgs e)
    {
        NewTabButton.Flyout?.Hide();
        ShowProjectEditor(null);
    }

    private async Task IndexKnownChecklistsAsync()
    {
        foreach (var entry in _recentChecklists.Entries.ToList())
        {
            if (FindTabByPath(entry.Path) is not null || !File.Exists(entry.Path))
                continue;

            try
            {
                var document = await _fileService.LoadAsync(entry.Path);
                _projects.Note(document, entry.Path);
                TrackObserved(document, entry.Path);
            }
            catch (ChecklistFileException)
            {
            }
        }

        RefreshProjectSurfaces();
    }

    private void ShowProjectEditor(Project? existing)
    {
        _editingProjectId = existing?.Id;
        _editorReturnToProject = existing is not null && !HomePanel.IsVisible;
        _editorReturn = HomePanel.IsVisible ? CurrentHomeSurface() : HomeSurface.Dashboard;
        if (_editorReturn == HomeSurface.CreateProject)
            _editorReturn = HomeSurface.Dashboard;

        var creating = existing is null;
        ProjectEditorTitle.Text = creating ? "Create project" : "Edit project";
        ProjectEditorHint.Text = creating
            ? "Organize multiple tasklists under one project."
            : "Update the name, icon, and details for this project.";
        SaveProjectButton.Content = creating ? "Create Project" : "Save";
        OpenProjectButton.IsVisible = creating;
        ProjectEditorHost.Content = new ProjectEditorForm(existing);
        ShowHomeScreen();
        ShowHomeSurface(HomeSurface.CreateProject);
    }

    private async void OpenProjectButton_Click(object? sender, RoutedEventArgs e)
    {
        var projects = _projects.Projects
            .OrderByDescending(project => project.UpdatedAt)
            .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (projects.Count == 0)
        {
            await ShowMessage("No saved projects yet. Create a project first.");
            return;
        }

        var choices = projects
            .Select(project => new TasklistChoice(
                project.Name,
                DescribeSavedProject(project),
                project.Id,
                null,
                false))
            .ToList();
        var choice = await TasklistPickerWindow.PickAsync(this, "Open project", choices, allowBrowse: false);
        if (choice is null || choice.Browse)
            return;

        var project = _projects.Find(choice.DocumentId);
        if (project is null)
        {
            await ShowMessage("That project could not be found.");
            return;
        }

        _editingProjectId = null;
        _editorReturnToProject = false;
        ProjectEditorHost.Content = null;
        OpenProject(project);
    }

    private string DescribeSavedProject(Project project)
    {
        var parts = new List<string>();
        if (project.Status == ProjectStatus.Archived)
            parts.Add("Archived");

        var stack = DescribePlatform(project);
        if (stack.Length > 0)
            parts.Add(stack);

        var count = _projects.ForProject(project.Id).Count;
        parts.Add(count == 1 ? "1 tasklist" : $"{count} tasklists");
        return string.Join(" · ", parts);
    }

    private void CloseProjectEditor_Click(object? sender, RoutedEventArgs e) =>
        CloseProjectEditor();

    private void CloseProjectEditor()
    {
        var id = _editingProjectId;
        var back = _editorReturn;
        var returnToProject = _editorReturnToProject;
        _editingProjectId = null;
        _editorReturnToProject = false;
        ProjectEditorHost.Content = null;

        if (returnToProject && id is Guid projectId)
        {
            var project = _projects.Find(projectId);
            if (project is not null)
            {
                OpenProject(project);
                return;
            }
        }

        ShowHomeScreen();
        if (back is HomeSurface.Projects or HomeSurface.Files or HomeSurface.Create)
            ShowHomeSurface(back);
    }

    private void SaveProjectEditor_Click(object? sender, RoutedEventArgs e)
    {
        if (ProjectEditorHost.Content is not ProjectEditorForm form)
            return;

        var draft = form.ReadDraft();
        if (draft is null)
            return;

        if (_editingProjectId is not Guid id)
        {
            try
            {
                var project = _projects.Create(draft);
                _editingProjectId = null;
                _editorReturnToProject = false;
                ProjectEditorHost.Content = null;
                OpenProject(project);
            }
            catch (ArgumentException)
            {
            }

            return;
        }

        var updated = _projects.Update(id, draft);
        if (updated is null)
        {
            CloseProjectEditor();
            return;
        }

        var tab = _projectTabs.FirstOrDefault(item => item.Project.Id == id);
        if (tab is not null)
        {
            tab.Project = updated;
            UpdateProjectHeader(tab);
        }

        RefreshProjectOverview();
        CloseProjectEditor();
    }

    private void OpenProject(Project project)
    {
        EnsureProjectTab(project, activate: true);
        _ = RefreshProjectFilesAsync(project.Id);
    }

    private async Task RefreshProjectFilesAsync(Guid projectId)
    {
        foreach (var record in _projects.ForProject(projectId).ToList())
        {
            if (_tabs.Any(tab => tab.Document.Id == record.DocumentId))
                continue;
            if (string.IsNullOrWhiteSpace(record.Path) || !File.Exists(record.Path))
                continue;

            try
            {
                var document = await _fileService.LoadAsync(record.Path);
                _projects.Note(document, record.Path);
            }
            catch (ChecklistFileException)
            {
            }
        }

        RefreshProjectOverview();
        if (_activeProjectTab?.Project.Id == projectId)
            ShowProject(_activeProjectTab);
    }

    private void ArchiveProject(ProjectTab tab)
    {
        var next = tab.Project.Status == ProjectStatus.Archived
            ? ProjectStatus.Active
            : ProjectStatus.Archived;
        var updated = _projects.SetStatus(tab.Project.Id, next);
        if (updated is null)
            return;

        tab.Project = updated;
        UpdateProjectHeader(tab);
        if (_activeProjectTab == tab)
            ShowProject(tab);
        RefreshProjectOverview();
        ShowToast(next == ProjectStatus.Archived
            ? $"Archived \"{updated.Name}\"."
            : $"Restored \"{updated.Name}\".");
    }

    private async Task DeleteProjectAsync(Project project)
    {
        var confirmed = await ConfirmAsync(
            "Delete project?",
            $"Delete \"{project.Name}\"? Its checklists stay on this computer as standalone checklists.",
            "Delete",
            "Cancel",
            danger: true);
        if (!confirmed)
            return;

        foreach (var record in _projects.ForProject(project.Id).ToList())
        {
            var tab = _tabs.FirstOrDefault(item => item.Document.Id == record.DocumentId);
            if (tab is not null)
            {
                if (!await ReleaseOpenTabAsync(tab, project.Id))
                    return;
                continue;
            }

            if (string.IsNullOrWhiteSpace(record.Path) || !File.Exists(record.Path))
            {
                _projects.ForgetTasklist(record.DocumentId);
                continue;
            }

            var document = await _fileService.LoadAsync(record.Path);
            if (document.ProjectId != project.Id)
                continue;

            document.ProjectId = null;
            document.UpdatedOn = DateTime.UtcNow;
            await _fileService.SaveAsync(record.Path, document, replaceDifferentDocument: false);
            _projects.Note(document, record.Path);
        }

        foreach (var tab in _tabs.ToList())
        {
            if (tab.Document.ProjectId != project.Id && tab.OpenedFromProjectId != project.Id)
                continue;

            if (!await ReleaseOpenTabAsync(tab, project.Id))
                return;
        }

        var wasViewing = _activeProjectTab?.Project.Id == project.Id;
        var before = CaptureTabs();
        var projectIds = CaptureProjectIds();
        var focus = TabSession.AfterProjectClosed(
            before,
            projectIds,
            wasViewing ? new TabFocus(null, project.Id) : CurrentFocus(),
            project.Id);

        ReminderSchedule.Remove(project.Id);
        ReminderSchedule.DismissDocument(project.Id);
        _projects.Delete(project.Id);
        var open = _projectTabs.FirstOrDefault(item => item.Project.Id == project.Id);
        if (open is not null)
            _projectTabs.Remove(open);

        ArrangeTabStrip();
        if (wasViewing)
            ApplyFocus(focus);

        RefreshProjectOverview();
        ShowToast($"Deleted \"{project.Name}\". Its checklists are still available.");
    }

    private async Task<bool> ReleaseOpenTabAsync(ChecklistTab tab, Guid projectId)
    {
        var parent = tab.OpenedFromProjectId;
        var belonged = tab.Document.ProjectId == projectId;
        if (!belonged && parent != projectId)
            return true;

        tab.Document.ProjectId = belonged ? null : tab.Document.ProjectId;
        if (parent == projectId)
            tab.OpenedFromProjectId = null;

        if (belonged && !string.IsNullOrWhiteSpace(tab.FilePath))
        {
            if (!await WriteDocumentAsync(tab, tab.FilePath, announce: false))
            {
                tab.Document.ProjectId = projectId;
                tab.OpenedFromProjectId = parent;
                return false;
            }
        }
        else if (belonged)
        {
            tab.IsDirty = true;
            UpdateTabChrome(tab);
        }

        return true;
    }

    private async Task AddTasklistAsync(Project project)
    {
        var choices = new List<TasklistChoice>();
        var seen = new HashSet<Guid>();
        foreach (var tab in _tabs)
        {
            if (tab.Document.ProjectId == project.Id || !seen.Add(tab.Document.Id))
                continue;

            choices.Add(new TasklistChoice(
                string.IsNullOrWhiteSpace(tab.View.WorkTitle) ? tab.Document.Title : tab.View.WorkTitle,
                DescribeMembership(tab.Document.ProjectId, open: true),
                tab.Document.Id,
                tab.FilePath,
                false));
        }

        foreach (var record in _projects.Tasklists)
        {
            if (record.ProjectId == project.Id || !seen.Add(record.DocumentId))
                continue;

            choices.Add(new TasklistChoice(
                record.Title,
                DescribeMembership(record.ProjectId, open: false),
                record.DocumentId,
                record.Path,
                false));
        }

        choices.Sort((left, right) => string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase));
        var choice = await TasklistPickerWindow.PickAsync(this, "Add a tasklist", choices, allowBrowse: true);
        if (choice is null)
            return;

        if (choice.Browse)
        {
            await BrowseTasklistAsync(project.Id);
            return;
        }

        await AssignChoiceAsync(project.Id, choice);
    }

    private async Task BrowseTasklistAsync(Guid projectId)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add checklist to project",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Super Dev checklist")
                {
                    Patterns = ["*.superdev.json", "*.json"],
                    MimeTypes = ["application/json"]
                }
            ]
        });
        if (files.Count == 0)
            return;

        var path = LocalPath(files[0]);
        if (path is null)
        {
            await ShowMessage("That location can't be used. Choose a file on this computer.");
            return;
        }

        var document = await _fileService.LoadAsync(path);
        var tab = _tabs.FirstOrDefault(item => item.Document.Id == document.Id) ?? FindTabByPath(path);
        if (tab is not null)
            await AssignOpenTabAsync(projectId, tab);
        else
            await AssignStoredAsync(projectId, document, path);
    }

    private async Task MoveTasklistAsync(Guid projectId, Guid documentId)
    {
        var others = _projects.Projects
            .Where(project => project.Id != projectId)
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (others.Count == 0)
        {
            await ShowMessage("Create another project before moving this checklist.");
            return;
        }

        var choices = others.Select(project => new TasklistChoice(
            project.Name,
            project.Status == ProjectStatus.Archived
                ? "Archived"
                : DescribePlatform(project),
            project.Id,
            null,
            false)).ToList();
        var choice = await TasklistPickerWindow.PickAsync(this, "Move tasklist", choices, allowBrowse: false);
        if (choice is null || choice.Browse)
            return;

        var tab = _tabs.FirstOrDefault(item => item.Document.Id == documentId);
        if (tab is not null)
        {
            await AssignOpenTabAsync(choice.DocumentId, tab);
            return;
        }

        var record = _projects.FindTasklist(documentId);
        if (record is null || string.IsNullOrWhiteSpace(record.Path) || !File.Exists(record.Path))
        {
            await ShowMessage("That checklist file could not be found.");
            return;
        }

        var document = await _fileService.LoadAsync(record.Path);
        await AssignStoredAsync(choice.DocumentId, document, record.Path);
    }

    private async Task AssignChoiceAsync(Guid projectId, TasklistChoice choice)
    {
        var tab = _tabs.FirstOrDefault(item => item.Document.Id == choice.DocumentId);
        if (tab is not null)
        {
            await AssignOpenTabAsync(projectId, tab);
            return;
        }

        if (string.IsNullOrWhiteSpace(choice.Path) || !File.Exists(choice.Path))
        {
            await ShowMessage("That checklist file could not be found.");
            _projects.ForgetTasklist(choice.DocumentId);
            RefreshProjectSurfaces();
            return;
        }

        var document = await _fileService.LoadAsync(choice.Path);
        await AssignStoredAsync(projectId, document, choice.Path);
    }

    private async Task AdoptTabIntoProjectAsync(Project project, ChecklistTab tab)
    {
        if (!_tabs.Contains(tab) || _projectTabs.All(item => item.Project.Id != project.Id))
            return;

        if (tab.OpenedFromProjectId == project.Id && tab.Document.ProjectId == project.Id)
        {
            ActivateTab(tab);
            return;
        }

        if (tab.Document.ProjectId != project.Id)
            await AssignOpenTabAsync(project.Id, tab);

        if (!_tabs.Contains(tab) || tab.Document.ProjectId != project.Id)
            return;

        if (tab.IsPinned)
        {
            tab.IsPinned = false;
            tab.PinButton.Classes.Remove("pinned");
            ToolTip.SetTip(tab.PinButton, "Pin tab");
        }

        tab.OpenedFromProjectId = project.Id;
        var host = _projectTabs.FirstOrDefault(item => item.Project.Id == project.Id);
        if (host is not null)
            host.ChildrenCollapsed = false;
        ArrangeTabStrip();
        ActivateTab(tab);
        RefreshProjectSurfaces();
    }

    private async Task AssignOpenTabAsync(Guid projectId, ChecklistTab tab, bool confirmMove = true)
    {
        if (tab.Document.ProjectId == projectId)
        {
            RefreshProjectSurfaces();
            return;
        }

        var title = string.IsNullOrWhiteSpace(tab.View.WorkTitle) ? tab.Document.Title : tab.View.WorkTitle;
        if (confirmMove && !await ConfirmMoveAsync(title, tab.Document.ProjectId, projectId))
            return;

        var previous = tab.Document.ProjectId;
        var previousParent = tab.OpenedFromProjectId;
        tab.Document.ProjectId = projectId;
        if (previousParent == previous)
        {
            var destination = _projects.Find(projectId);
            if (destination is not null)
                EnsureProjectTab(destination, activate: false);
            tab.OpenedFromProjectId = projectId;
        }

        if (!string.IsNullOrWhiteSpace(tab.FilePath))
        {
            if (!await WriteDocumentAsync(tab, tab.FilePath, announce: false))
            {
                tab.Document.ProjectId = previous;
                tab.OpenedFromProjectId = previousParent;
                return;
            }
        }
        else
        {
            tab.IsDirty = true;
            UpdateTabChrome(tab);
        }

        ArrangeTabStrip();
        RefreshProjectSurfaces();
    }

    private async Task AssignStoredAsync(Guid projectId, ChecklistDocument document, string path)
    {
        if (document.ProjectId == projectId)
        {
            _projects.Note(document, path);
            RefreshProjectSurfaces();
            return;
        }

        if (!await ConfirmMoveAsync(document.Title, document.ProjectId, projectId))
            return;

        document.ProjectId = projectId;
        document.UpdatedOn = DateTime.UtcNow;
        await _fileService.SaveAsync(path, document, replaceDifferentDocument: false);
        RememberChecklist(path, document, trackActivity: true);
        RefreshProjectSurfaces();
    }

    private async Task RemoveTasklistAsync(Guid projectId, Guid documentId)
    {
        var tab = _tabs.FirstOrDefault(item => item.Document.Id == documentId);
        if (tab is not null)
        {
            if (tab.Document.ProjectId != projectId && tab.OpenedFromProjectId != projectId)
                return;

            var openTitle = string.IsNullOrWhiteSpace(tab.View.WorkTitle) ? tab.Document.Title : tab.View.WorkTitle;
            if (!await ConfirmRemoveTasklistAsync(projectId, openTitle))
                return;

            var parent = tab.OpenedFromProjectId;
            var hadProject = tab.Document.ProjectId == projectId;
            if (hadProject)
                tab.Document.ProjectId = null;
            if (parent == projectId)
                tab.OpenedFromProjectId = null;

            if (hadProject && !string.IsNullOrWhiteSpace(tab.FilePath))
            {
                if (!await WriteDocumentAsync(tab, tab.FilePath, announce: false))
                {
                    tab.Document.ProjectId = projectId;
                    tab.OpenedFromProjectId = parent;
                    return;
                }
            }
            else if (hadProject)
            {
                tab.IsDirty = true;
                UpdateTabChrome(tab);
            }

            ArrangeTabStrip();
            RefreshProjectSurfaces();
            return;
        }

        var record = _projects.FindTasklist(documentId);
        if (record is null)
            return;

        if (!await ConfirmRemoveTasklistAsync(projectId, record.Title))
            return;

        if (string.IsNullOrWhiteSpace(record.Path) || !File.Exists(record.Path))
        {
            _projects.ForgetTasklist(documentId);
            RefreshProjectSurfaces();
            return;
        }

        var document = await _fileService.LoadAsync(record.Path);
        if (document.ProjectId == projectId)
        {
            document.ProjectId = null;
            document.UpdatedOn = DateTime.UtcNow;
            await _fileService.SaveAsync(record.Path, document, replaceDifferentDocument: false);
            RememberChecklist(record.Path, document, trackActivity: true);
        }

        RefreshProjectSurfaces();
    }

    private async Task<bool> ConfirmRemoveTasklistAsync(Guid projectId, string title)
    {
        var projectName = _projects.Find(projectId)?.Name ?? "this project";
        return await ConfirmAsync(
            "Remove tasklist?",
            $"Remove \"{title}\" from {projectName}? The checklist stays on this computer.",
            "Remove",
            "Cancel",
            danger: true);
    }

    private async Task<bool> ConfirmMoveAsync(string title, Guid? fromId, Guid toId)
    {
        if (fromId is not Guid from || from == toId)
            return true;

        var fromName = _projects.Find(from)?.Name ?? "another project";
        var toName = _projects.Find(toId)?.Name ?? "this project";
        return await ConfirmAsync(
            "Move tasklist?",
            $"Move \"{title}\" from {fromName} to {toName}?",
            "Move",
            "Cancel");
    }

    private string DescribeMembership(Guid? projectId, bool open)
    {
        if (projectId is not Guid id)
            return open ? "Open checklist" : "Standalone checklist";

        var name = _projects.Find(id)?.Name ?? "another project";
        return open ? $"Open · in {name}" : $"In {name}";
    }

    private static string DescribePlatform(Project project)
    {
        var framework = project.Framework?.Trim() ?? string.Empty;
        var platform = project.Platform?.Trim() ?? string.Empty;
        if (framework.Length > 0 && platform.Length > 0)
            return $"{framework} · {platform}";
        if (framework.Length > 0)
            return framework;
        if (platform.Length > 0)
            return platform;
        return string.Empty;
    }

    private async Task OpenProjectTasklistAsync(Guid projectId, Guid documentId)
    {
        var open = _tabs.FirstOrDefault(tab => tab.Document.Id == documentId);
        if (open is not null)
        {
            ActivateTab(open);
            return;
        }

        var record = _projects.FindTasklist(documentId);
        if (record is null || string.IsNullOrWhiteSpace(record.Path) || !File.Exists(record.Path))
        {
            await ShowMessage("That checklist file could not be found.");
            return;
        }

        var byPath = FindTabByPath(record.Path);
        if (byPath is not null)
        {
            ActivateTab(byPath);
            return;
        }

        var document = await _fileService.LoadAsync(record.Path);
        RememberChecklist(record.Path, document);
        var parent = document.ProjectId == projectId ? projectId : (Guid?)null;
        parent = TabSession.ParentFor(alreadyOpen: false, existingParent: null, parent);
        OpenDocumentTab(document, record.Path, isDirty: false, parent);
    }

    private void RefreshProjectSurfaces()
    {
        if (HomePanel.IsVisible)
            RefreshProjectOverview();

        if (_activeProjectTab is not null && WorkspaceContent.Content == _activeProjectTab.Detail)
            ShowProject(_activeProjectTab);
    }

    private void RefreshProjectOverview()
    {
        var projects = _projects.Projects.OrderByDescending(project => project.UpdatedAt).ToList();
        var any = projects.Count > 0;
        ProjectEmptyText.IsVisible = !any;
        ProjectCards.IsVisible = any;
        FillProjectCards(ProjectCards, projects.Take(4));
        if (AllProjectsPanel.IsVisible)
        {
            AllProjectsEmpty.IsVisible = !any;
            AllProjectCards.IsVisible = any;
            FillProjectCards(AllProjectCards, projects);
        }
    }

    private void FillProjectCards(Grid host, IEnumerable<Project> projects)
    {
        host.Children.Clear();
        host.ColumnDefinitions.Clear();
        host.RowDefinitions.Clear();
        var cards = projects.Select(project =>
        {
            var mine = Snapshots(project.Id);
            var progress = ProjectProgress.Measure(
                mine.Where(item => !item.Missing).Select(item => item.Progress));
            progress = progress with { TasklistCount = mine.Count };
            return ProjectCard(project, progress);
        }).ToList();

        if (cards.Count == 0)
            return;

        var columns = cards.Count == 1 ? 1 : 2;
        for (var column = 0; column < columns; column++)
            host.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

        var rows = (cards.Count + columns - 1) / columns;
        for (var row = 0; row < rows; row++)
            host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var index = 0; index < cards.Count; index++)
        {
            Grid.SetColumn(cards[index], index % columns);
            Grid.SetRow(cards[index], index / columns);
            host.Children.Add(cards[index]);
        }
    }

    private Control ProjectCard(Project project, ProjectProgressReport report)
    {
        var color = ProjectChrome.Brush(project.Color);
        var archived = project.Status == ProjectStatus.Archived;
        var title = new TextBlock
        {
            Text = project.Name,
            FontSize = AppearanceService.Font(15),
            FontWeight = FontWeight.SemiBold,
            Foreground = AppearanceService.Brush(AppearanceService.TextPrimaryBrush),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var heading = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        heading.Children.Add(title);
        var stack = DescribePlatform(project);
        if (stack.Length > 0)
        {
            heading.Children.Add(new TextBlock
            {
                Text = stack,
                FontSize = AppearanceService.Font(12),
                Foreground = AppearanceService.Brush(AppearanceService.TextMutedBrush),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        var mark = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(12),
            Background = color,
            VerticalAlignment = VerticalAlignment.Center,
            Child = ProjectChrome.Icon(project.Icon, Brushes.White, 18)
        };
        var badge = new Border
        {
            Background = AppearanceService.Brush(archived
                ? AppearanceService.CardBrush
                : AppearanceService.SuccessSubtleBrush),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Avalonia.Controls.Shapes.Ellipse
                    {
                        Width = 6,
                        Height = 6,
                        VerticalAlignment = VerticalAlignment.Center,
                        Fill = AppearanceService.Brush(archived
                            ? AppearanceService.TextFaintBrush
                            : AppearanceService.SuccessBrush)
                    },
                    new TextBlock
                    {
                        Text = archived ? "Archived" : "Active",
                        FontSize = AppearanceService.Font(12),
                        FontWeight = FontWeight.SemiBold,
                        Foreground = AppearanceService.Brush(archived
                            ? AppearanceService.TextMutedBrush
                            : AppearanceService.SuccessTextBrush),
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
        var reserve = new Border { Width = 18 };
        var top = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            ColumnSpacing = 12
        };
        Grid.SetColumn(heading, 1);
        Grid.SetColumn(badge, 2);
        Grid.SetColumn(reserve, 3);
        top.Children.Add(mark);
        top.Children.Add(heading);
        top.Children.Add(badge);
        top.Children.Add(reserve);

        var counts = new TextBlock
        {
            Text = report.TasklistCount == 1
                ? "1 tasklist"
                : $"{report.TasklistCount} tasklists",
            FontSize = AppearanceService.Font(12),
            Foreground = AppearanceService.Brush(AppearanceService.TextMutedBrush),
            VerticalAlignment = VerticalAlignment.Center
        };
        var percent = new TextBlock
        {
            Text = report.PercentLabel,
            FontSize = AppearanceService.Font(13),
            FontWeight = FontWeight.SemiBold,
            Foreground = AppearanceService.Brush(AppearanceService.TextPrimaryBrush),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var numbers = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 12, 0, 6)
        };
        numbers.Children.Add(counts);
        Grid.SetColumn(percent, 1);
        numbers.Children.Add(percent);

        var body = new StackPanel
        {
            Children =
            {
                top,
                numbers,
                new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = report.Percent,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Foreground = color,
                    Background = AppearanceService.Brush(AppearanceService.BorderSubtleBrush)
                }
            }
        };

        var button = new Button
        {
            Classes = { "project-card" },
            Content = body,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        if (project.Status == ProjectStatus.Archived)
            button.Classes.Add("archived");

        var tip = string.IsNullOrWhiteSpace(project.Description)
            ? project.Name
            : $"{project.Name} — {project.Description}";
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) =>
        {
            var current = _projects.Find(project.Id);
            if (current is not null)
                OpenProject(current);
        };

        var menu = ProjectMenuButton(project);
        var host = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { button, menu }
        };
        return host;
    }

    private Button ProjectMenuButton(Project project)
    {
        var dots = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        for (var i = 0; i < 3; i++)
        {
            dots.Children.Add(new Avalonia.Controls.Shapes.Ellipse
            {
                Classes = { "glyph" },
                Width = 3.5,
                Height = 3.5
            });
        }

        var id = project.Id;
        var edit = new MenuItem { Header = "Edit" };
        edit.Click += (_, _) => EditFromCard(id);
        var archive = new MenuItem
        {
            Header = project.Status == ProjectStatus.Archived ? "Restore" : "Archive"
        };
        archive.Click += (_, _) => ArchiveFromCard(id);
        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => DeleteFromCard(id);

        var button = new Button
        {
            Classes = { "card-menu" },
            Content = dots,
            Flyout = new MenuFlyout
            {
                Items = { edit, archive, delete }
            }
        };
        ToolTip.SetTip(button, "Project actions");
        return button;
    }

    private void EditFromCard(Guid id)
    {
        var project = _projects.Find(id);
        if (project is not null)
            ShowProjectEditor(project);
    }

    private void ArchiveFromCard(Guid id)
    {
        var project = _projects.Find(id);
        if (project is null)
            return;

        var tab = _projectTabs.FirstOrDefault(item => item.Project.Id == id);
        if (tab is not null)
        {
            tab.Project = project;
            ArchiveProject(tab);
            return;
        }

        var next = project.Status == ProjectStatus.Archived
            ? ProjectStatus.Active
            : ProjectStatus.Archived;
        var updated = _projects.SetStatus(id, next);
        if (updated is null)
            return;

        RefreshProjectOverview();
        ShowToast(next == ProjectStatus.Archived
            ? $"Archived \"{updated.Name}\"."
            : $"Restored \"{updated.Name}\".");
    }

    private void DeleteFromCard(Guid id)
    {
        var project = _projects.Find(id);
        if (project is not null)
            _ = DeleteProjectAsync(project);
    }

    private void ShowProject(ProjectTab tab)
    {
        var snapshots = Snapshots(tab.Project.Id);
        var report = ProjectProgress.Measure(snapshots.Where(item => !item.Missing).Select(item => item.Progress));
        var openIds = _tabs.Select(item => item.Document.Id).ToHashSet();
        var rows = snapshots.Select(item => new ProjectTasklistRow(
            item.DocumentId,
            item.Title,
            item.Missing ? "File missing" : SnapshotDetail(item.Progress),
            PercentOf(item.Progress),
            item.Missing,
            openIds.Contains(item.DocumentId))).ToList();
        tab.Detail.Render(tab.Project, report, rows);
    }

    private void SaveProjectSchedule(Project project)
    {
        project.UpdatedAt = DateTime.UtcNow;
        _projects.Remember(project);
        PublishProjectDeadline(project);
        PublishProjectReminder(project);
        if (RemindersPanel.IsVisible)
            RefreshReminderRows();
    }

    private void ApplyProjectReminderEdit(Project project, ScheduledReminder reminder, ReminderEditResult edit)
    {
        if (string.IsNullOrEmpty(reminder.Slot))
        {
            project.ReminderOn = edit.Removed ? null : edit.NextUtc;
            project.ReminderEveryMinutes = edit.Removed ? 0 : edit.EveryMinutes;
            project.ReminderSilent = !edit.Removed && edit.Silent;
        }
        else if (reminder.Slot == ScheduledReminder.DeadlineSlot)
        {
            if (edit.Removed)
                project.NotifyBeforeMinutes = null;
            project.DeadlineSilent = !edit.Removed && edit.Silent && project.NotifyBeforeMinutes is not null;
        }
        else
        {
            return;
        }

        project.UpdatedAt = DateTime.UtcNow;
        _projects.Remember(project);
        PublishProjectDeadline(project);
        PublishProjectReminder(project);
        if (_activeProjectTab?.Project.Id == project.Id)
            ShowProject(_activeProjectTab);
        RefreshReminderRows();
    }

    private void SyncProjectSchedules()
    {
        foreach (var project in _projects.Projects)
        {
            SyncStoredProjectReminder(project);
            PublishProjectDeadline(project);
        }
    }

    private void SyncStoredProjectReminder(Project project)
    {
        if (project.ReminderOn is not { } current)
        {
            ReminderSchedule.RemoveSlot(project.Id, string.Empty);
            return;
        }

        var every = Math.Max(0, project.ReminderEveryMinutes);
        var scheduled = ReminderSchedule.Find(project.Id);
        if (scheduled is not null && scheduled.EveryMinutes == every && scheduled.NextOn > current)
        {
            project.ReminderOn = scheduled.NextOn;
            project.UpdatedAt = DateTime.UtcNow;
            _projects.Remember(project);
            ReminderSchedule.Touch(project.Id, string.Empty, ProjectTitle(project), null, project.ReminderSilent);
            RefreshOpenProject(project.Id);
            return;
        }

        if (scheduled is null && ReminderSchedule.WasDelivered(project.Id, current))
        {
            if (every <= 0)
            {
                project.ReminderOn = null;
                project.ReminderEveryMinutes = 0;
                project.ReminderSilent = false;
            }
            else
            {
                project.ReminderOn = ReminderSchedule.Advance(current, every, DateTime.UtcNow);
                PublishProjectReminder(project);
            }

            project.UpdatedAt = DateTime.UtcNow;
            _projects.Remember(project);
            RefreshOpenProject(project.Id);
            return;
        }

        if (scheduled is null ||
            scheduled.NextOn != current ||
            scheduled.EveryMinutes != every ||
            scheduled.Silent != project.ReminderSilent ||
            scheduled.Title != ProjectTitle(project))
        {
            PublishProjectReminder(project);
        }
    }

    private void PublishProjectReminder(Project project)
    {
        if (project.ReminderOn is not { } next)
        {
            ReminderSchedule.RemoveSlot(project.Id, string.Empty);
            return;
        }

        ReminderSchedule.Upsert(new ScheduledReminder
        {
            DocumentId = project.Id,
            Title = ProjectTitle(project),
            Detail = "Project",
            NextOn = next,
            EveryMinutes = Math.Max(0, project.ReminderEveryMinutes),
            Silent = project.ReminderSilent
        });
    }

    private static void PublishProjectDeadline(Project project)
    {
        var wanted = new List<ScheduledReminder>();
        if (project.Deadline is { } date && project.NotifyBeforeMinutes is int lead)
        {
            wanted.Add(new ScheduledReminder
            {
                DocumentId = project.Id,
                Slot = ScheduledReminder.DeadlineSlot,
                Title = ProjectTitle(project),
                Detail = DeadlineValue.NotifyText(lead),
                NextOn = DeadlineValue.Instant(date, project.DeadlineMinute).AddMinutes(-Math.Max(0, lead)),
                EveryMinutes = 0,
                Silent = project.DeadlineSilent
            });
        }

        ReminderSchedule.ReplaceAttached(project.Id, wanted);
    }

    private void RefreshOpenProject(Guid projectId)
    {
        if (_activeProjectTab?.Project.Id == projectId)
            ShowProject(_activeProjectTab);
    }

    private static string ProjectTitle(Project project) =>
        string.IsNullOrWhiteSpace(project.Name) ? "Project" : project.Name.Trim();

    private static string SnapshotDetail(TasklistProgressInput progress)
    {
        var state = ProjectProgress.StateOf(progress) switch
        {
            TasklistRunState.Completed => "Completed",
            TasklistRunState.InProgress => "In progress",
            _ => "Not started"
        };
        var checks = progress.Total == 0
            ? "No checks yet"
            : $"{progress.Completed} / {progress.Total} checks";
        if (progress.Skipped > 0)
            checks += $", {progress.Skipped} skipped";
        return $"{state} · {checks}";
    }

    private static double PercentOf(TasklistProgressInput progress) =>
        progress.Total <= 0 ? 0 : (double)progress.Completed / progress.Total * 100d;

    private List<TasklistSnapshot> Snapshots(Guid? projectId)
    {
        var result = new List<TasklistSnapshot>();
        var seen = new HashSet<Guid>();
        foreach (var tab in _tabs)
        {
            if (tab.Document.ProjectId is not Guid id)
                continue;
            if (projectId is Guid filter && id != filter)
                continue;
            if (!seen.Add(tab.Document.Id))
                continue;

            result.Add(new TasklistSnapshot
            {
                DocumentId = tab.Document.Id,
                Path = tab.FilePath,
                Title = string.IsNullOrWhiteSpace(tab.View.WorkTitle) ? tab.Document.Title : tab.View.WorkTitle,
                Progress = TasklistProgressInput.FromItems(tab.Document.IsCompleted, tab.View.Items),
                Missing = false,
                Order = _projects.FindTasklist(tab.Document.Id)?.Order ?? 0
            });
        }

        foreach (var record in _projects.Tasklists)
        {
            if (record.ProjectId is not Guid id)
                continue;
            if (projectId is Guid filter && id != filter)
                continue;
            if (!seen.Add(record.DocumentId))
                continue;

            var missing = string.IsNullOrWhiteSpace(record.Path) || !File.Exists(record.Path);
            result.Add(new TasklistSnapshot
            {
                DocumentId = record.DocumentId,
                Path = record.Path,
                Title = record.Title,
                Progress = missing
                    ? default
                    : new TasklistProgressInput(
                        record.IsCompleted,
                        record.CompletedChecks,
                        record.TotalChecks,
                        record.SkippedChecks),
                Missing = missing,
                Order = record.Order
            });
        }

        result.Sort((left, right) =>
        {
            var order = left.Order.CompareTo(right.Order);
            return order != 0
                ? order
                : string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    private void ApplyOverviewOrder(Guid projectId, IReadOnlyList<Guid> documentIds)
    {
        _projects.Reorder(projectId, documentIds);
        if (ReorderOpenChildren(projectId, documentIds))
            ArrangeTabStrip();
        if (_activeProjectTab?.Project.Id == projectId)
            ShowProject(_activeProjectTab);
    }

    private bool ReorderOpenChildren(Guid projectId, IReadOnlyList<Guid> documentIds)
    {
        var rank = new Dictionary<Guid, int>();
        for (var i = 0; i < documentIds.Count; i++)
            rank[documentIds[i]] = i;

        var slots = new List<int>();
        var children = new List<ChecklistTab>();
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].OpenedFromProjectId != projectId)
                continue;

            slots.Add(i);
            children.Add(_tabs[i]);
        }

        if (children.Count < 2)
            return false;

        var sorted = children
            .OrderBy(tab => rank.TryGetValue(tab.Document.Id, out var index) ? index : int.MaxValue)
            .ToList();
        var changed = false;
        for (var i = 0; i < slots.Count; i++)
        {
            if (ReferenceEquals(_tabs[slots[i]], sorted[i]))
                continue;

            _tabs[slots[i]] = sorted[i];
            changed = true;
        }

        return changed;
    }

    private ProjectTab EnsureProjectTab(Project project, bool activate)
    {
        var existing = _projectTabs.FirstOrDefault(tab => tab.Project.Id == project.Id);
        if (existing is not null)
        {
            existing.Project = project;
            UpdateProjectHeader(existing);
            if (activate)
                ActivateProject(existing);
            return existing;
        }

        var created = BuildProjectTab(project);
        _projectTabs.Add(created);
        ArrangeTabStrip();
        if (activate)
            ActivateProject(created);
        return created;
    }

    private ProjectTab BuildProjectTab(Project project)
    {
        var colorBar = new Border
        {
            Width = 4,
            Height = 16,
            Margin = new Thickness(0, 8),
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Background = ProjectChrome.Brush(project.Color),
            RenderTransformOrigin = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            RenderTransform = new ScaleTransform { ScaleX = 1, ScaleY = 0 }
        };
        var foldGlyph = new Avalonia.Controls.Shapes.Path
        {
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stroke = AppearanceService.Brush(AppearanceService.TextMutedBrush),
            Data = Geometry.Parse("M4,2 L9,6 L4,10")
        };
        var fold = new Button
        {
            Classes = { "project-fold" },
            Content = foldGlyph,
            IsVisible = false
        };
        var iconHost = new Panel
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center
        };
        var title = new TextBlock
        {
            MaxWidth = 140,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var close = new Button { Content = "×" };
        close.Classes.Add("tab-close");
        ToolTip.SetTip(close, "Close project");
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"),
            ColumnSpacing = 6,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetColumn(fold, 1);
        Grid.SetColumn(iconHost, 2);
        Grid.SetColumn(title, 3);
        Grid.SetColumn(close, 4);
        headerGrid.Children.Add(colorBar);
        headerGrid.Children.Add(fold);
        headerGrid.Children.Add(iconHost);
        headerGrid.Children.Add(title);
        headerGrid.Children.Add(close);

        var header = new Border
        {
            Classes = { "workspace-tab", "project-tab" },
            Height = 40,
            Padding = new Thickness(8, 3),
            BorderThickness = new Thickness(2),
            Child = headerGrid,
            VerticalAlignment = VerticalAlignment.Center
        };
        var childRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        var group = new Border
        {
            Classes = { "project-group" },
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { header, childRow }
            }
        };
        var detail = new ProjectDetailView();
        var tab = new ProjectTab(project, header, title, colorBar, iconHost, fold, foldGlyph, childRow, group, detail);
        fold.Click += (_, e) =>
        {
            e.Handled = true;
            tab.ChildrenCollapsed = !tab.ChildrenCollapsed;
            ArrangeTabStrip();
        };
        fold.AddHandler(
            PointerPressedEvent,
            (_, e) => e.Handled = true,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        detail.EditRequested += (_, _) => ShowProjectEditor(tab.Project);
        detail.ArchiveRequested += (_, _) => ArchiveProject(tab);
        detail.DeleteRequested += (_, _) => _ = RunFileOperation(() => DeleteProjectAsync(tab.Project));
        detail.AddRequested += (_, _) => _ = RunFileOperation(() => AddTasklistAsync(tab.Project));
        detail.OpenRequested += (_, documentId) => _ = RunFileOperation(() => OpenProjectTasklistAsync(tab.Project.Id, documentId));
        detail.MoveRequested += (_, documentId) => _ = RunFileOperation(() => MoveTasklistAsync(tab.Project.Id, documentId));
        detail.RemoveRequested += (_, documentId) => _ = RunFileOperation(() => RemoveTasklistAsync(tab.Project.Id, documentId));
        detail.ScheduleChanged += (_, _) => SaveProjectSchedule(tab.Project);
        detail.Reordered += (_, ids) =>
        {
            if (ids is not null)
                ApplyOverviewOrder(tab.Project.Id, ids);
        };
        header.PointerPressed += (_, e) =>
        {
            if (IsInsideButton(e.Source))
                return;
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
                return;

            ActivateProject(tab);
            e.Handled = true;
        };
        close.Click += (_, e) =>
        {
            e.Handled = true;
            _ = RunFileOperation(() => CloseProjectTabAsync(tab));
        };
        close.AddHandler(
            PointerPressedEvent,
            (_, e) => e.Handled = true,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        UpdateProjectHeader(tab);
        return tab;
    }

    private void UpdateProjectHeader(ProjectTab tab)
    {
        tab.TitleText.Text = $"Project: {tab.Project.Name}";
        var color = ProjectChrome.Brush(tab.Project.Color);
        tab.ColorBar.Background = color;
        tab.Header.BorderBrush = color;
        tab.Header.BorderThickness = new Thickness(2);
        tab.IconHost.Children.Clear();
        tab.IconHost.Children.Add(ProjectChrome.Icon(
            tab.Project.Icon,
            AppearanceService.Brush(AppearanceService.TextPrimaryBrush),
            16));
        var tip = string.IsNullOrWhiteSpace(tab.Project.Description)
            ? tab.Project.Name
            : $"{tab.Project.Name} — {tab.Project.Description}";
        ToolTip.SetTip(tab.Header, tip);
        ToolTip.SetTip(tab.TitleText, tip);
        foreach (var child in _tabs.Where(item => item.Document.ProjectId == tab.Project.Id))
            UpdateTabChrome(child);
        foreach (var child in _tabs.Where(item => item.OpenedFromProjectId == tab.Project.Id))
            StyleChecklistHeader(child);
    }

    private void ActivateProject(ProjectTab tab)
    {
        _returnToHistory = false;
        HideHistory();
        _activeProjectTab = tab;
        _activeTab = null;
        ClearChecklistSelection();
        ClearProjectSelection();
        tab.Header.Classes.Add("selected");
        _settingsOpen = false;
        _shortcutsOpen = false;
        _settingsOverChecklist = false;
        _settingsOverReminders = false;
        _remindersOpen = false;
        RemindersButton.Classes.Remove("selected");
        RemindersPanel.IsVisible = false;
        SettingsPanel.IsVisible = false;
        ShortcutsPanel.IsVisible = false;
        ShowProject(tab);
        WorkspaceContent.Content = tab.Detail;
        WorkspaceContent.IsVisible = true;
        TabBar.IsVisible = true;
        HomePanel.IsVisible = false;
        SetChecklistActionsEnabled(false);
        UpdateManualSaveVisibility();
        SyncProjectIndicators();
        SyncHomeTab();
    }

    private void SyncProjectIndicators()
    {
        var live = new HashSet<Border>();
        foreach (var project in _projectTabs)
        {
            live.Add(project.ColorBar);
            AnimateProjectBar(project.ColorBar, ProjectWindowVisible(project));
        }

        foreach (var bar in _projectBars.Keys.ToArray())
        {
            if (!live.Contains(bar))
                _projectBars.Remove(bar);
        }
    }

    private void AnimateProjectBar(Border bar, bool open)
    {
        if (bar.RenderTransform is not ScaleTransform scale)
            return;

        var to = open ? 1d : 0d;
        if (_projectBars.TryGetValue(bar, out var motion))
        {
            if (Math.Abs(motion.To - to) < 0.001)
                return;

            motion.From = scale.ScaleY;
            motion.To = to;
            motion.StartedTicks = Environment.TickCount64;
        }
        else
        {
            if (Math.Abs(scale.ScaleY - to) < 0.001)
                return;

            _projectBars[bar] = new ProjectBarMotion(scale, scale.ScaleY, to);
        }

        if (_projectBarClock)
            return;

        _projectBarClock = true;
        Dispatcher.UIThread.Post(StepProjectBars, DispatcherPriority.Render);
    }

    private void StepProjectBars()
    {
        var moving = false;
        var now = Environment.TickCount64;
        foreach (var pair in _projectBars.ToArray())
        {
            var motion = pair.Value;
            var elapsed = now - motion.StartedTicks;
            var t = ProjectBarDuration.TotalMilliseconds <= 0
                ? 1d
                : Math.Clamp(elapsed / ProjectBarDuration.TotalMilliseconds, 0d, 1d);
            var eased = t * t * (3d - 2d * t);
            motion.Scale.ScaleY = motion.From + (motion.To - motion.From) * eased;
            if (t >= 1d)
                _projectBars.Remove(pair.Key);
            else
                moving = true;
        }

        if (moving)
            Dispatcher.UIThread.Post(StepProjectBars, DispatcherPriority.Render);
        else
            _projectBarClock = false;
    }

    private bool ProjectWindowVisible(ProjectTab tab) =>
        WorkspaceContent.IsVisible
        && ReferenceEquals(WorkspaceContent.Content, tab.Detail)
        && !HomePanel.IsVisible
        && !SettingsPanel.IsVisible
        && !ShortcutsPanel.IsVisible
        && !RemindersPanel.IsVisible;

    private async Task CloseProjectTabAsync(ProjectTab project)
    {
        if (!_projectTabs.Contains(project))
            return;

        var children = _tabs.Where(tab => tab.OpenedFromProjectId == project.Project.Id).ToList();
        var closeTasklists = false;
        if (children.Count > 0)
        {
            var noun = children.Count == 1 ? "tasklist" : "tasklists";
            var dialog = new StyledDialog(
                "Close project?",
                $"\"{project.Project.Name}\" has {children.Count} open {noun}. Close only the project, or close those {noun} too?",
                "Project only",
                "Cancel",
                "Also close tasklists");
            dialog.Width = 640;
            var choice = await dialog.ShowDialog<ClosePromptResult>(this);
            if (choice == ClosePromptResult.Cancel)
                return;

            closeTasklists = choice == ClosePromptResult.Discard;
        }

        if (closeTasklists)
        {
            foreach (var child in children)
            {
                child.AutoSaveTimer?.Stop();
                while (child.AutoSaveTask is { } pending)
                    await pending;
            }

            var plan = ProjectClose.Plan(children
                .Select(child => new TabCloseRequest(child.Document.Id, HasSavedFile(child), child.IsDirty))
                .ToList());
            foreach (var step in plan)
            {
                if (step.Kind == TabCloseStepKind.Close)
                    continue;

                var child = children.FirstOrDefault(tab => tab.Document.Id == step.DocumentId);
                if (child is null || !_tabs.Contains(child))
                    return;

                if (step.Kind == TabCloseStepKind.SaveExisting)
                {
                    if (string.IsNullOrWhiteSpace(child.FilePath) ||
                        !await WriteDocumentAsync(child, child.FilePath, announce: false))
                        return;
                }
                else if (step.Kind == TabCloseStepKind.Prompt)
                {
                    var unsaved = await PromptUnsavedAsync(child.Document.Title);
                    if (unsaved == ClosePromptResult.Cancel)
                        return;
                    if (unsaved == ClosePromptResult.Save && !await SaveAsync(child))
                        return;
                }
            }
        }
        else
        {
            foreach (var child in children)
                child.OpenedFromProjectId = null;
        }

        var childWasActive = closeTasklists
            && _activeTab is not null
            && children.Any(tab => tab.Document.Id == _activeTab.Document.Id);
        var projectWasActive = _activeProjectTab == project;
        var before = CaptureTabs();
        var focus = TabSession.AfterProjectClosed(
            before,
            CaptureProjectIds(),
            projectWasActive
                ? new TabFocus(null, project.Project.Id)
                : childWasActive
                    ? new TabFocus(_activeTab!.Document.Id, null)
                    : CurrentFocus(),
            project.Project.Id);

        if (closeTasklists)
        {
            foreach (var child in children)
                DetachChecklist(child);
        }

        _projectTabs.Remove(project);
        if (project.Group.Parent is Panel parent)
            parent.Children.Remove(project.Group);

        ArrangeTabStrip();
        if (projectWasActive || childWasActive)
            ApplyFocus(focus);
        else
            TabBar.IsVisible = true;

        SyncProjectIndicators();
        RefreshRecentList();
        RefreshNotificationBell();
        RefreshProjectSurfaces();
    }

    private void ArrangeTabStrip()
    {
        CancelTabSlides();
        foreach (var tab in _tabs)
        {
            if (tab.OpenedFromProjectId is Guid projectId &&
                _projectTabs.All(project => project.Project.Id != projectId))
                tab.OpenedFromProjectId = null;
        }

        foreach (var project in _projectTabs)
            project.ChildRow.Children.Clear();

        var pinned = new List<ChecklistTab>();
        var loose = new List<ChecklistTab>();
        var children = new List<ChecklistTab>();
        foreach (var tab in _tabs)
        {
            if (ShowsAsChild(tab))
                children.Add(tab);
            else if (tab.IsPinned)
                pinned.Add(tab);
            else
                loose.Add(tab);
        }

        _tabs.Clear();
        _tabs.AddRange(pinned);
        _tabs.AddRange(loose);
        _tabs.AddRange(children);
        TabsPanel.Children.Clear();

        foreach (var tab in pinned)
        {
            StyleChecklistHeader(tab);
            tab.HeaderContainer.VerticalAlignment = VerticalAlignment.Center;
            TabsPanel.Children.Add(tab.HeaderContainer);
        }

        foreach (var project in _projectTabs)
        {
            foreach (var child in children.Where(tab => tab.OpenedFromProjectId == project.Project.Id))
            {
                StyleChecklistHeader(child);
                project.ChildRow.Children.Add(child.HeaderContainer);
            }

            var hasChildren = project.ChildRow.Children.Count > 0;
            project.FoldButton.IsVisible = hasChildren;
            project.ChildRow.IsVisible = hasChildren && !project.ChildrenCollapsed;
            project.FoldGlyph.Data = Geometry.Parse(project.ChildrenCollapsed
                ? "M4,2 L9,6 L4,10"
                : "M8,2 L3,6 L8,10");
            ToolTip.SetTip(
                project.FoldButton,
                project.ChildrenCollapsed ? "Show this project's tasklists" : "Hide this project's tasklists");
            if (project.Group.Parent is Panel groupParent)
                groupParent.Children.Remove(project.Group);
            TabsPanel.Children.Add(project.Group);
        }

        foreach (var tab in loose)
        {
            StyleChecklistHeader(tab);
            tab.HeaderContainer.VerticalAlignment = VerticalAlignment.Center;
            TabsPanel.Children.Add(tab.HeaderContainer);
        }

        TabBar.IsVisible = true;
        UpdatePinVisibility();
    }

    private bool ShowsAsChild(ChecklistTab tab) =>
        tab.OpenedFromProjectId is Guid projectId && _projectTabs.Any(project => project.Project.Id == projectId);

    private void StyleChecklistHeader(ChecklistTab tab)
    {
        if (!ShowsAsChild(tab))
        {
            tab.HeaderContainer.Classes.Remove("child-tab");
            tab.HeaderContainer.ClearValue(Border.BorderBrushProperty);
            tab.HeaderContainer.ClearValue(Border.BorderThicknessProperty);
            UpdateProjectCaption(tab);
            return;
        }

        tab.HeaderContainer.Classes.Add("child-tab");
        var project = _projectTabs.First(item => item.Project.Id == tab.OpenedFromProjectId);
        var color = ProjectChrome.Brush(project.Project.Color);
        tab.HeaderContainer.BorderBrush = color;
        tab.HeaderContainer.BorderThickness = new Thickness(2);
        tab.HeaderContainer.VerticalAlignment = VerticalAlignment.Center;
        UpdateProjectCaption(tab);
    }

    private void ClearProjectSelection()
    {
        foreach (var project in _projectTabs)
            project.Header.Classes.Remove("selected");
    }

    private void ClearChecklistSelection()
    {
        foreach (var tab in _tabs)
            tab.HeaderContainer.Classes.Remove("selected");
    }

    private void DetachChecklist(ChecklistTab tab)
    {
        if (!_tabs.Contains(tab))
            return;

        tab.AutoSaveTimer?.Stop();
        if (tab.HeaderContainer.Parent is Panel parent)
            parent.Children.Remove(tab.HeaderContainer);
        _tabs.Remove(tab);
        if (_activeTab == tab)
            _activeTab = null;
        if (string.IsNullOrWhiteSpace(tab.FilePath))
            ReminderSchedule.Remove(tab.Document.Id);
    }

    private void ApplyFocus(TabFocus focus)
    {
        if (focus.DocumentId is Guid documentId)
        {
            var tab = _tabs.FirstOrDefault(item => item.Document.Id == documentId);
            if (tab is not null)
            {
                ActivateTab(tab);
                return;
            }
        }

        if (focus.ProjectId is Guid projectId)
        {
            var project = _projectTabs.FirstOrDefault(item => item.Project.Id == projectId);
            if (project is not null)
            {
                ActivateProject(project);
                return;
            }
        }

        _activeTab = null;
        _activeProjectTab = null;
        ShowHomeScreen();
    }

    private TabFocus CurrentFocus()
    {
        if (_activeProjectTab is not null)
            return new TabFocus(null, _activeProjectTab.Project.Id);
        if (_activeTab is not null)
            return new TabFocus(_activeTab.Document.Id, null);
        return TabFocus.Home;
    }

    private List<OpenChecklistTab> CaptureTabs() =>
        _tabs.Select(tab => new OpenChecklistTab(tab.Document.Id, tab.OpenedFromProjectId, tab.IsPinned)).ToList();

    private List<Guid> CaptureProjectIds() =>
        _projectTabs.Select(tab => tab.Project.Id).ToList();

    private void SelectVisualTab(int delta)
    {
        var order = VisualFocus();
        if (order.Count == 0)
            return;

        var current = -1;
        for (var i = 0; i < order.Count; i++)
        {
            var item = order[i];
            if (_activeProjectTab is not null && item.ProjectId == _activeProjectTab.Project.Id)
            {
                current = i;
                break;
            }

            if (_activeTab is not null && item.DocumentId == _activeTab.Document.Id)
            {
                current = i;
                break;
            }
        }

        var next = current < 0
            ? (delta > 0 ? 0 : order.Count - 1)
            : (current + delta + order.Count) % order.Count;
        ApplyFocus(order[next]);
    }

    private List<TabFocus> VisualFocus()
    {
        var order = new List<TabFocus>();
        foreach (var tab in _tabs.Where(item => !ShowsAsChild(item) && item.IsPinned))
            order.Add(new TabFocus(tab.Document.Id, null));

        foreach (var project in _projectTabs)
        {
            order.Add(new TabFocus(null, project.Project.Id));
            foreach (var child in _tabs.Where(item => item.OpenedFromProjectId == project.Project.Id))
                order.Add(new TabFocus(child.Document.Id, null));
        }

        foreach (var tab in _tabs.Where(item => !ShowsAsChild(item) && !item.IsPinned))
            order.Add(new TabFocus(tab.Document.Id, null));

        return order;
    }

    private int DropIndexAmongChildren(double pointerX, ChecklistTab dragging)
    {
        if (dragging.OpenedFromProjectId is not Guid projectId)
            return 0;

        var index = 0;
        foreach (var other in _tabs)
        {
            if (other == dragging || other.OpenedFromProjectId != projectId)
                continue;

            var x = LayoutStripX(other.HeaderContainer);
            if (pointerX > x + other.HeaderContainer.Bounds.Width / 2)
                index++;
        }

        return index;
    }

    private void SlideChildTabs(ChecklistTab dragging, int target)
    {
        if (dragging.OpenedFromProjectId is not Guid projectId)
            return;

        var siblings = _tabs.Where(tab => tab.OpenedFromProjectId == projectId).ToList();
        var from = siblings.IndexOf(dragging);
        if (from < 0)
            return;

        var spacing = _projectTabs.FirstOrDefault(project => project.Project.Id == projectId)?.ChildRow.Spacing ?? 8;
        var pitch = dragging.HeaderContainer.Bounds.Width + spacing;
        var siblingHeaders = new HashSet<Border>(siblings.Select(tab => tab.HeaderContainer));
        for (var i = 0; i < siblings.Count; i++)
        {
            if (i == from)
                continue;

            var offset = 0d;
            if (target > from && i > from && i <= target)
                offset = -pitch;
            else if (target < from && i >= target && i < from)
                offset = pitch;

            AnimateTabSlide(siblings[i].HeaderContainer, offset);
        }

        foreach (var open in _tabs)
        {
            if (open == dragging || siblingHeaders.Contains(open.HeaderContainer))
                continue;

            var shift = (open.HeaderContainer.RenderTransform as TranslateTransform)?.X ?? 0;
            if (Math.Abs(shift) > 0.5 || _slideTarget.ContainsKey(open.HeaderContainer))
                AnimateTabSlide(open.HeaderContainer, 0);
        }
    }

    private void CommitChildDrop(ChecklistTab dragging, int target)
    {
        CancelTabSlides();
        if (dragging.OpenedFromProjectId is Guid projectId)
        {
            var siblings = _tabs.Where(tab => tab.OpenedFromProjectId == projectId).ToList();
            var from = siblings.IndexOf(dragging);
            if (from >= 0 && target >= 0 && target < siblings.Count && target != from)
            {
                siblings.RemoveAt(from);
                siblings.Insert(target, dragging);
                var rebuilt = new List<ChecklistTab>(_tabs.Count);
                var placed = false;
                foreach (var tab in _tabs)
                {
                    if (tab.OpenedFromProjectId == projectId)
                    {
                        if (!placed)
                        {
                            rebuilt.AddRange(siblings);
                            placed = true;
                        }

                        continue;
                    }

                    rebuilt.Add(tab);
                }

                _tabs.Clear();
                _tabs.AddRange(rebuilt);
            }
        }

        ClearDragTransforms();
        ArrangeTabStrip();
    }

    private void ReleaseChildToStandalone(ChecklistTab dragging, double pointerX)
    {
        CancelTabSlides();
        dragging.OpenedFromProjectId = null;
        dragging.IsPinned = false;
        dragging.PinButton.Classes.Remove("pinned");
        ToolTip.SetTip(dragging.PinButton, "Pin tab");

        var loose = _tabs.Where(tab => tab != dragging && tab.OpenedFromProjectId is null && !tab.IsPinned).ToList();
        var index = 0;
        foreach (var other in loose)
        {
            var x = LayoutStripX(other.HeaderContainer);
            if (pointerX > x + other.HeaderContainer.Bounds.Width / 2)
                index++;
        }

        loose.Insert(index, dragging);
        var pinned = _tabs.Where(tab => tab != dragging && tab.OpenedFromProjectId is null && tab.IsPinned).ToList();
        var children = _tabs.Where(tab => tab != dragging && tab.OpenedFromProjectId is not null).ToList();
        _tabs.Clear();
        _tabs.AddRange(pinned);
        _tabs.AddRange(loose);
        _tabs.AddRange(children);
        ClearDragTransforms();
        ArrangeTabStrip();
        ActivateTab(dragging);
        RefreshProjectSurfaces();
    }

    private void ClearDragTransforms()
    {
        foreach (var open in _tabs)
        {
            if (open.HeaderContainer.RenderTransform is TranslateTransform translate)
            {
                translate.X = 0;
                translate.Y = 0;
            }

            open.HeaderContainer.ZIndex = 0;
        }
    }

    private int DropIndexAmongStandalones(double pointerX, ChecklistTab dragging)
    {
        var index = 0;
        foreach (var other in _tabs)
        {
            if (other.OpenedFromProjectId is not null || other.IsPinned || other == dragging)
                continue;

            var origin = other.HeaderContainer.TranslatePoint(new Point(0, 0), TabsPanel);
            var x = origin?.X ?? other.HeaderContainer.Bounds.X;
            if (pointerX > x + other.HeaderContainer.Bounds.Width / 2)
                index++;
        }

        return index;
    }

    private void SlideStandaloneTabs(ChecklistTab dragging, int target)
    {
        var unpinned = _tabs.Where(tab => tab.OpenedFromProjectId is null && !tab.IsPinned).ToList();
        var from = unpinned.IndexOf(dragging);
        if (from < 0)
            return;

        var pitch = dragging.HeaderContainer.Bounds.Width + TabsPanel.Spacing;
        for (var i = 0; i < unpinned.Count; i++)
        {
            if (i == from)
                continue;

            var offset = 0d;
            if (target > from && i > from && i <= target)
                offset = -pitch;
            else if (target < from && i >= target && i < from)
                offset = pitch;

            AnimateTabSlide(unpinned[i].HeaderContainer, offset);
        }
    }

    private void CommitStandaloneDrop(ChecklistTab dragging, int target)
    {
        CancelTabSlides();
        var unpinned = _tabs.Where(tab => tab.OpenedFromProjectId is null && !tab.IsPinned).ToList();
        var from = unpinned.IndexOf(dragging);
        if (from >= 0 && target >= 0 && target < unpinned.Count && target != from)
        {
            unpinned.RemoveAt(from);
            unpinned.Insert(target, dragging);
            var pinned = _tabs.Where(tab => tab.OpenedFromProjectId is null && tab.IsPinned).ToList();
            var children = _tabs.Where(tab => tab.OpenedFromProjectId is not null).ToList();
            _tabs.Clear();
            _tabs.AddRange(pinned);
            _tabs.AddRange(unpinned);
            _tabs.AddRange(children);
        }

        foreach (var open in _tabs)
        {
            if (open.HeaderContainer.RenderTransform is TranslateTransform translate)
            {
                translate.X = 0;
                translate.Y = 0;
            }

            open.HeaderContainer.ZIndex = 0;
        }

        ArrangeTabStrip();
    }

    private sealed class ProjectBarMotion(ScaleTransform scale, double from, double to)
    {
        public ScaleTransform Scale { get; } = scale;

        public double From { get; set; } = from;

        public double To { get; set; } = to;

        public long StartedTicks { get; set; } = Environment.TickCount64;
    }

    private sealed class TasklistSnapshot
    {
        public Guid DocumentId { get; init; }

        public string? Path { get; init; }

        public string Title { get; init; } = string.Empty;

        public TasklistProgressInput Progress { get; init; }

        public bool Missing { get; init; }

        public int Order { get; init; }
    }

    private sealed class ProjectTab(
        Project project,
        Border header,
        TextBlock titleText,
        Border colorBar,
        Panel iconHost,
        Button foldButton,
        Avalonia.Controls.Shapes.Path foldGlyph,
        StackPanel childRow,
        Border group,
        ProjectDetailView detail)
    {
        public Project Project { get; set; } = project;

        public Border Header { get; } = header;

        public TextBlock TitleText { get; } = titleText;

        public Border ColorBar { get; } = colorBar;

        public Panel IconHost { get; } = iconHost;

        public Button FoldButton { get; } = foldButton;

        public Avalonia.Controls.Shapes.Path FoldGlyph { get; } = foldGlyph;

        public bool ChildrenCollapsed { get; set; }

        public StackPanel ChildRow { get; } = childRow;

        public Border Group { get; } = group;

        public ProjectDetailView Detail { get; } = detail;
    }
}
