using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using SuperDev.Models;
using SuperDev.Services;
using SuperDev.Services.Activity;

namespace SuperDev.Views;

public partial class MainWindow
{
    private ActivityDatabase? _activityDb;
    private ActivityTrackingService? _activity;
    private HistoryStatisticsService? _historyStats;
    private HistoryViewModel? _historyModel;
    private HistoryView? _historyView;
    private bool _historyOpen;
    private bool _returnToHistory;
    private int _historyRefresh;

    private void InitializeHistory()
    {
        try
        {
            _activityDb = new ActivityDatabase(ActivityDatabase.DefaultPath());
            _activity = new ActivityTrackingService(_activityDb);
            _historyStats = new HistoryStatisticsService(_activityDb);
        }
        catch (Exception ex) when (IsActivityError(ex))
        {
            _activityDb = null;
            _activity = null;
            _historyStats = null;
        }

        _historyModel = new HistoryViewModel();
        Closed += (_, _) => _activityDb?.Dispose();
    }

    private void HomeHistory_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ShowHistory();

    private void ShowHistory()
    {
        if (_surfaceDepth > 0)
            return;

        _returnToHistory = false;
        _historyOpen = true;
        _settingsOpen = false;
        _shortcutsOpen = false;
        _settingsOverChecklist = false;
        _settingsOverReminders = false;
        _remindersOpen = false;
        RemindersButton.Classes.Remove("selected");
        SettingsPanel.IsVisible = false;
        ShortcutsPanel.IsVisible = false;
        RemindersPanel.IsVisible = false;
        HomePanel.IsVisible = false;
        WorkspaceContent.IsVisible = false;
        WorkspaceContent.Content = null;
        ClearChecklistSelection();
        ClearProjectSelection();
        TabBar.IsVisible = true;
        SetChecklistActionsEnabled(false);
        UpdateManualSaveVisibility();
        EnsureHistoryView();
        HistoryHost.IsVisible = true;
        SyncProjectIndicators();
        SyncHomeTab();
        _ = RefreshHistoryAsync();
    }

    /// <summary>
    /// Hides History without closing, recreating, or resetting open project and tasklist documents.
    /// </summary>
    private void HideHistory()
    {
        _historyOpen = false;
        HistoryHost.IsVisible = false;
    }

    private void EnsureHistoryView()
    {
        if (_historyView is not null)
            return;

        var view = new HistoryView();
        view.BackRequested += (_, _) =>
        {
            _returnToHistory = false;
            ShowHomeScreen();
        };
        view.WeekRequested += (_, _) =>
        {
            _historyModel?.SelectThisWeek();
            _ = RefreshHistoryAsync();
        };
        view.MonthRequested += (_, _) =>
        {
            _historyModel?.SelectThisMonth();
            _ = RefreshHistoryAsync();
        };
        view.CustomPeriodRequested += (_, _) =>
        {
            _historyModel?.BeginCustom();
            _ = RefreshHistoryAsync();
        };
        view.CustomDatesChanged += (_, _) =>
        {
            if (_historyModel is null || view.CustomStartDate is not DateOnly start || view.CustomEndDate is not DateOnly end)
                return;

            if (!_historyModel.TryUseCustom(start, end))
            {
                view.ShowRangeError(_historyModel.RangeError);
                return;
            }

            _ = RefreshHistoryAsync();
        };
        view.GranularityChanged += (_, _) =>
        {
            _historyModel?.SelectGranularity(view.RequestedGranularity);
            _ = RefreshHistoryAsync();
        };
        view.ProjectChanged += (_, _) =>
        {
            _historyModel?.SelectProject(view.SelectedScope, view.SelectedProjectId);
            _ = RefreshHistoryAsync();
        };
        view.SearchChanged += (_, _) =>
        {
            _historyModel?.SetSearch(view.SearchText);
            _ = RefreshHistoryAsync();
        };
        view.WorkTypeChanged += (_, _) =>
        {
            _historyModel?.SetWorkType(view.SelectedWorkType);
            _ = RefreshHistoryAsync();
        };
        view.ViewAllRequested += (_, _) =>
        {
            if (_historyModel is null)
                return;

            _historyModel.SetShowingAll(!_historyModel.ShowingAllCompleted);
            _ = RefreshHistoryAsync();
        };
        view.OpenRequested += (_, entry) => _ = OpenHistoryTasklistAsync(entry);
        _historyView = view;
        HistoryHost.Content = view;
    }

    private async Task RefreshHistoryAsync()
    {
        if (!_historyOpen || _historyModel is null || _historyView is null)
            return;

        var generation = ++_historyRefresh;
        var names = ProjectNames();
        var projects = ProjectOptions();
        var query = _historyModel.CreateQuery(names);
        var state = new HistoryViewState
        {
            Period = _historyModel.SelectedPeriod,
            CustomStart = _historyModel.CustomStart,
            CustomEnd = _historyModel.CustomEnd,
            RangeError = _historyModel.RangeError,
            ProjectScope = _historyModel.ProjectScope,
            ProjectId = _historyModel.ProjectId,
            Projects = projects,
            Search = _historyModel.Search,
            WorkType = _historyModel.WorkType,
            ShowingAllCompleted = _historyModel.ShowingAllCompleted
        };

        HistoryDashboard dashboard;
        try
        {
            if (_historyStats is null)
            {
                dashboard = EmptyDashboard(query);
            }
            else
            {
                var stats = _historyStats;
                dashboard = await Task.Run(() => stats.Query(query));
            }
        }
        catch (Exception ex) when (IsActivityError(ex))
        {
            return;
        }

        if (generation != _historyRefresh || !_historyOpen)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _historyRefresh || !_historyOpen || _historyView is null)
                return;

            _historyView.Apply(dashboard, state);
        });
    }

    private void RefreshHistoryIfOpen()
    {
        if (_historyOpen)
            _ = RefreshHistoryAsync();
    }

    private async Task OpenHistoryTasklistAsync(CompletedTasklistEntry entry)
    {
        var open = _tabs.FirstOrDefault(tab => tab.Document.Id == entry.TasklistId);
        if (open is not null)
        {
            ActivateTab(open);
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.FilePath) || !File.Exists(entry.FilePath))
        {
            ShowToast("That checklist isn't available to open.");
            return;
        }

        await RunFileOperation(() => OpenChecklistPathAsync(entry.FilePath));
    }

    private void TrackCreated(ChecklistDocument document, string? path = null)
    {
        if (_activity is null)
            return;

        try
        {
            _activity.RecordCreated(document, path, projectName: ProjectLabel(document.ProjectId));
            RefreshHistoryIfOpen();
        }
        catch (Exception ex) when (IsActivityError(ex))
        {
        }
    }

    private void TrackObserved(ChecklistDocument document, string? path)
    {
        if (_activity is null)
            return;

        try
        {
            _activity.ObserveExisting(document, path);
        }
        catch (Exception ex) when (IsActivityError(ex))
        {
        }
    }

    private void TrackCaptured(ChecklistDocument document, string? path)
    {
        if (_activity is null)
            return;

        try
        {
            _activity.Capture(document, path, projectName: ProjectLabel(document.ProjectId));
            RefreshHistoryIfOpen();
        }
        catch (Exception ex) when (IsActivityError(ex))
        {
        }
    }

    private void TrackDeleted(Guid tasklistId, Guid? projectId)
    {
        if (_activity is null)
            return;

        try
        {
            _activity.RecordDeleted(tasklistId, projectId);
            RefreshHistoryIfOpen();
        }
        catch (Exception ex) when (IsActivityError(ex))
        {
        }
    }

    private async Task<Guid?> ReplacedDocumentIdAsync(string path, Guid newDocumentId, bool replacing)
    {
        if (!replacing)
            return null;

        try
        {
            var previous = await _fileService.LoadAsync(path);
            return previous.Id == newDocumentId ? null : previous.Id;
        }
        catch (ChecklistFileException)
        {
            return null;
        }
    }

    private string? ProjectLabel(Guid? projectId) =>
        projectId is Guid id ? _projects.Find(id)?.Name : null;

    private Dictionary<Guid, string> ProjectNames()
    {
        var names = new Dictionary<Guid, string>();
        foreach (var project in _projects.Projects)
            names[project.Id] = project.Name;
        return names;
    }

    private List<HistoryProjectOption> ProjectOptions()
    {
        var options = new List<HistoryProjectOption>();
        foreach (var project in _projects.Projects.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase))
        {
            options.Add(new HistoryProjectOption
            {
                Scope = HistoryProjectScope.Project,
                ProjectId = project.Id,
                Name = project.Name
            });
        }

        return options;
    }

    private static HistoryDashboard EmptyDashboard(HistoryQuery query) =>
        new()
        {
            Range = query.Range,
            Granularity = query.Granularity ?? query.Range.ChooseGranularity(),
            Trend = []
        };

    private static bool IsActivityError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException;
}
