using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Chrome;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SuperDev.Models;
using SuperDev.Services;
using SuperDev.Services.Ai;

namespace SuperDev.Views;

public partial class MainWindow : Window
{
    private readonly ChecklistTemplateService _checklistService = new();
    private readonly OllamaService _localAi = OllamaService.CreateLocal();
    private readonly ChecklistReviewService _reviews;
    private readonly TaskExplainService _explanations;
    private readonly AIModelManager _aiModels;
    private readonly AISetupManager _aiSetup;
    private bool _aiSetupBusy;
    private readonly ChecklistFileService _fileService = new();
    private readonly RecentChecklistService _recentChecklists = new();
    private readonly List<ChecklistTab> _tabs = new();

    private readonly List<Button> _templateButtons = [];
    private string? _selectedTemplateId;
    private ChecklistTab? _activeTab;
    private ChecklistTab? _dragTab;
    private Avalonia.Point _dragStart;
    private double _dragOriginX;
    private int _dragTarget = -1;
    private bool _tabDragging;
    private bool _dragCanReorder;
    private bool _dragReleaseChild;
    private double _dragPointerX;
    private ProjectTab? _dragProject;
    private Border? _dragLayer;
    private bool _slideClock;
    private int _slideEpoch;
    private readonly Dictionary<Border, double> _slideTarget = new();
    private bool _fileOperationInProgress;
    private readonly Queue<string> _pendingFileOpens = new();
    private bool _settingsOpen;
    private bool _shortcutsOpen;
    private int _surfaceDepth;
    private bool _settingsOverChecklist;
    private bool _settingsOverReminders;
    private bool _remindersOpen;
    private readonly HashSet<Key> _keysDown = new();
    private string? _lastShortcutId;
    private long _lastShortcutAt;
    private NativeMenuItem? _saveMenuItem;
    private NativeMenuItem? _exportJsonMenuItem;
    private NativeMenuItem? _exportCsvMenuItem;
    private NativeMenuItem? _copyTextMenuItem;
    private NativeMenuItem? _addCheckMenuItem;
    private NativeMenuItem? _saveTemplateMenuItem;
    private NativeMenuItem? _rearrangeMenuItem;
    private NativeMenuItem? _copyToMenuItem;
    private NativeMenuItem? _deleteChecksMenuItem;
    private NativeMenuItem? _pinTabMenuItem;
    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _reminderTimer;
    private DateTime _daemonEnsuredUtc = DateTime.MinValue;
    private bool _notificationListOpen;
    private string? _openNoticeKeys;
    private DateTime _lastSaveToastUtc = DateTime.MinValue;
    private bool _updateBusy;
    private bool _restartingForUpdate;
    private bool _automaticUpdateStarted;
    private bool _syncingUpdateOptions;
    private UpdateCheckResult? _availableUpdate;
    private const int AutoSaveDelayMilliseconds = 800;
    private static readonly TimeSpan SaveToastInterval = TimeSpan.FromMinutes(30);

    public MainWindow()
    {
        _reviews = new ChecklistReviewService(_localAi);
        _explanations = new TaskExplainService(_localAi);
        _aiModels = new AIModelManager(_localAi);
        _aiSetup = AISetupManager.Create(_localAi);
        InitializeComponent();
        InitializeHistory();
        BindChecklistMenuItems();
        ConfigureTitleChrome();
        BuildShortcutSettings();
        BuildAccentSwatches();
        SyncAppearanceSelection();
        AppearanceService.Changed += (_, _) =>
        {
            SyncAppearanceSelection();
            BuildShortcutSettings();
            RefreshRecentList();
            RefreshProjectOverview();
            foreach (var project in _projectTabs)
                UpdateProjectHeader(project);
            if (_activeProjectTab is not null && WorkspaceContent.Content == _activeProjectTab.Detail)
                ShowProject(_activeProjectTab);
            BuildTemplateCards(_selectedTemplateId);
            foreach (var tab in _tabs)
                tab.View.ApplyAppearance();
            RefreshHistoryIfOpen();
        };
        AddHandler(KeyDownEvent, ShortcutKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, ShortcutKeyUp, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _keysDown.Clear();
        UpdateManualSaveVisibility();
        RefreshRecentList();
        RefreshProjectOverview();
        BuildTemplateCards(null);
        var createProject = AppShortcuts.All.First(item => item.Id == AppShortcuts.CreateProject);
        var createTasklist = AppShortcuts.All.First(item => item.Id == AppShortcuts.NewChecklist);
        CreateProjectShortcut.Text = createProject.CurrentDisplay;
        CreateTasklistShortcut.Text = createTasklist.CurrentDisplay;
        ToolTip.SetTip(HomeEmptyCreateProjectButton, AppShortcuts.WithShortcut(AppShortcuts.CreateProject));
        ToolTip.SetTip(AllProjectsEmptyCreateProjectButton, AppShortcuts.WithShortcut(AppShortcuts.CreateProject));
        ToolTip.SetTip(NewTabButton, $"Create a project ({createProject.CurrentDisplay}) or tasklist ({createTasklist.CurrentDisplay})");
        ToolTip.SetTip(OpenChecklistButton, AppShortcuts.WithShortcut(AppShortcuts.OpenChecklist));
        StartReminderTimer();
        BindUpdateSettings();
        if (Program.OwnsActivationServer)
        {
            InstanceBridge.StartServer(request =>
                Dispatcher.UIThread.Post(() => OpenFromNotification(request.DocumentId, request.FilePath)));
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Program.StartupActivations.Count > 0)
        {
            foreach (var request in Program.StartupActivations)
                OpenFromNotification(request.DocumentId, request.FilePath);
        }

        _ = IndexKnownChecklistsAsync();
        if (!_automaticUpdateStarted &&
            (UpdateService.CheckAutomatically || UpdateService.InstallAutomatically))
        {
            _automaticUpdateStarted = true;
            _ = RunUpdateCheckAsync(interactive: false, automatic: UpdateService.InstallAutomatically);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var tab in _tabs)
        {
            if (string.IsNullOrWhiteSpace(tab.FilePath))
                ReminderSchedule.Remove(tab.Document.Id);
        }

        _localAi.Dispose();
        base.OnClosed(e);
    }

    private void BindChecklistMenuItems()
    {
        var menu = NativeMenu.GetMenu(this);
        if (menu is null)
            return;

        foreach (var top in menu.Items.OfType<NativeMenuItem>())
        {
            if (top.Menu is null)
                continue;

            foreach (var item in top.Menu.Items.OfType<NativeMenuItem>())
            {
                switch (item.Header as string)
                {
                    case "New Checklist":
                        ApplyMenuGesture(item, AppShortcuts.NewChecklist);
                        break;
                    case "Create Project":
                        ApplyMenuGesture(item, AppShortcuts.CreateProject);
                        break;
                    case "Open…":
                        ApplyMenuGesture(item, AppShortcuts.OpenChecklist);
                        break;
                    case "Save":
                        _saveMenuItem = item;
                        ApplyMenuGesture(item, AppShortcuts.Save);
                        break;
                    case "Export as JSON…":
                        _exportJsonMenuItem = item;
                        break;
                    case "Export as CSV…":
                        _exportCsvMenuItem = item;
                        break;
                    case "Copy as Text":
                        _copyTextMenuItem = item;
                        break;
                    case "Settings":
                        ApplyMenuGesture(item, AppShortcuts.Settings);
                        break;
                    case "Add Check":
                        _addCheckMenuItem = item;
                        ApplyMenuGesture(item, AppShortcuts.AddItem);
                        break;
                    case "Save as template":
                        _saveTemplateMenuItem = item;
                        ApplyMenuGesture(item, AppShortcuts.SaveAsTemplate);
                        break;
                    case "Rearrange…":
                        _rearrangeMenuItem = item;
                        ApplyMenuGesture(item, AppShortcuts.Rearrange);
                        break;
                    case "Copy to…":
                        _copyToMenuItem = item;
                        break;
                    case "Delete…":
                        _deleteChecksMenuItem = item;
                        break;
                    case "Pin Tab":
                        _pinTabMenuItem = item;
                        break;
                }
            }
        }
    }

    private static void ApplyMenuGesture(NativeMenuItem item, string id)
    {
        var shortcut = AppShortcuts.All.First(entry => entry.Id == id);
        item.Gesture = shortcut.MenuGesture;
    }

    private void WorkflowButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id || ResolveTemplate(id) is null)
            return;

        _selectedTemplateId = id;
        ClearTemplateSelection();
        button.Classes.Add("selected");

        if (id == "daily")
            WorkTitleTextBox.Text = DailyTitle(DateTime.Today);
    }

    private void ClearTemplateSelection()
    {
        foreach (var button in _templateButtons)
            button.Classes.Remove("selected");
    }

    private void BuildTemplateCards(string? selectId)
    {
        TemplateList.Children.Clear();
        _templateButtons.Clear();

        foreach (var template in _checklistService.Templates)
            TemplateList.Children.Add(CreateTemplateCard(template, canEdit: true));

        TemplateList.Children.Add(CreateTemplateCard(DailyTemplate(), canEdit: false));
        TemplateList.Children.Add(CreateTemplateCard(BlankTemplate(), canEdit: false));
        TemplateList.Children.Add(CreateNewTemplateCard());

        _selectedTemplateId = null;
        if (selectId is null)
            return;

        var match = _templateButtons.FirstOrDefault(button => button.Tag as string == selectId);
        if (match is null)
            return;

        _selectedTemplateId = selectId;
        match.Classes.Add("selected");
    }

    private static StackPanel TemplateCardContent(string icon, string title, string? description)
    {
        var content = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(new TextBlock
        {
            Text = icon,
            Classes = { "template-icon" }
        });
        content.Children.Add(new TextBlock
        {
            Text = title,
            Classes = { "template-title" }
        });
        if (!string.IsNullOrWhiteSpace(description))
        {
            content.Children.Add(new TextBlock
            {
                Text = description,
                Classes = { "template-description" }
            });
        }

        return content;
    }

    private Control CreateTemplateCard(ChecklistTemplate template, bool canEdit)
    {
        var button = new Button
        {
            Classes = { "template" },
            Tag = template.Id,
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Content = TemplateCardContent(template.Icon, template.Title, ChecklistTemplateService.PredefinedDescription(template.Id))
        };
        button.Click += WorkflowButton_Click;
        _templateButtons.Add(button);

        var host = new Grid
        {
            Width = 148,
            Height = 118,
            Margin = new Thickness(5)
        };
        host.Children.Add(button);

        if (canEdit)
        {
            var edit = new Button
            {
                Classes = { "template-edit" },
                Content = "✎",
                Tag = template.Id,
                ZIndex = 2,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };
            ToolTip.SetTip(edit, "Edit template");
            edit.Click += EditTemplate_Click;
            host.Children.Add(edit);
        }

        return host;
    }

    private Control CreateNewTemplateCard()
    {
        var button = new Button
        {
            Classes = { "template" },
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Content = TemplateCardContent("＋", "New template", description: null)
        };
        button.Click += NewTemplate_Click;

        return new Grid
        {
            Width = 148,
            Height = 118,
            Margin = new Thickness(5),
            Children = { button }
        };
    }

    private async Task SaveTabAsTemplateAsync(ChecklistTab tab)
    {
        tab.View.CommitDetails();
        var draft = new ChecklistTemplate
        {
            Title = tab.View.WorkTitle,
            Icon = IconForChecklist(tab.View),
            WorkType = WorkType.Custom,
            Items = tab.View.Items
                .Select(item => new TemplateCheck
                {
                    Title = item.Title,
                    Category = item.Category,
                    Priority = item.Priority
                })
                .ToList()
        };

        var dialog = new EditTemplateWindow(draft, _checklistService, creating: true);
        var result = await dialog.ShowDialog<TemplateEditResult>(this);
        if (result == TemplateEditResult.Created)
            BuildTemplateCards(dialog.ResultId);
    }

    private string IconForChecklist(ChecklistView view)
    {
        if (!string.IsNullOrWhiteSpace(view.TemplateName))
        {
            var named = _checklistService.Templates.FirstOrDefault(template =>
                string.Equals(template.Title, view.TemplateName, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(named?.Icon))
                return named.Icon;
        }

        return view.WorkType switch
        {
            WorkType.Bug => "🐛",
            WorkType.Feature => "✨",
            WorkType.Development => "💻",
            WorkType.Testing => "🧪",
            WorkType.PullRequest => "🔀",
            WorkType.Daily => "📅",
            WorkType.Blank => "📄",
            _ => "📋"
        };
    }

    private async void NewTemplate_Click(object? sender, RoutedEventArgs e)
    {
        var draft = new ChecklistTemplate
        {
            Icon = "📋",
            WorkType = WorkType.Custom
        };
        var dialog = new EditTemplateWindow(draft, _checklistService, creating: true);
        var result = await dialog.ShowDialog<TemplateEditResult>(this);
        if (result == TemplateEditResult.Created)
            BuildTemplateCards(dialog.ResultId);
    }

    private async void EditTemplate_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
            return;

        var template = _checklistService.Find(id);
        if (template is null)
            return;

        var dialog = new EditTemplateWindow(template, _checklistService);
        var result = await dialog.ShowDialog<TemplateEditResult>(this);
        var select = _selectedTemplateId;
        if (result == TemplateEditResult.Created)
            select = dialog.ResultId;
        else if (result == TemplateEditResult.Deleted && select == id)
            select = null;

        if (result != TemplateEditResult.Cancel)
            BuildTemplateCards(select);
    }

    private ChecklistTemplate? ResolveTemplate(string? id) =>
        id switch
        {
            "daily" => DailyTemplate(),
            "blank" => BlankTemplate(),
            _ => _checklistService.Find(id)
        };

    private static ChecklistTemplate DailyTemplate() => new()
    {
        Id = "daily",
        Title = "Daily",
        Icon = "📅",
        WorkType = WorkType.Daily
    };

    private static ChecklistTemplate BlankTemplate() => new()
    {
        Id = "blank",
        Title = "Blank Checklist",
        Icon = "📄",
        WorkType = WorkType.Blank
    };

    private async void Create_Click(
        object? sender,
        RoutedEventArgs e)
    {
        var template = ResolveTemplate(_selectedTemplateId);
        if (template is null)
        {
            await ShowMessage("Please select a template.");
            return;
        }

        if (template.Id == "daily")
        {
            if (await CreateDailyTrackerAsync())
                ClearCreateForm();
            return;
        }

        var workTitle = WorkTitleTextBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(workTitle))
        {
            await ShowMessage("Please enter a title for your work.");
            return;
        }

        var now = DateTime.UtcNow;
        var document = new ChecklistDocument
        {
            FormatVersion = ChecklistDocument.CurrentFormatVersion,
            Id = Guid.NewGuid(),
            Title = workTitle,
            WorkType = template.WorkType,
            TemplateName = template.Title,
            CreatedOn = now,
            UpdatedOn = now,
            IsCompleted = false,
            Items = template.Id == "blank" ? [] : _checklistService.CreateItems(template)
        };

        TrackCreated(document);
        OpenDocumentTab(document, filePath: null, isDirty: true);
        ClearCreateForm();
    }

    private async void GenerateWithAi_Click(object? sender, RoutedEventArgs e)
    {
        if (_aiSetupBusy)
            return;

        if (!await EnsureLocalAiAsync(this))
            return;

        var dialog = new GenerateChecklistWindow(_localAi, _aiModels);
        var draft = await dialog.ShowDialog<AiChecklistDraft?>(this);
        if (draft is null || draft.Items.Count == 0 || string.IsNullOrWhiteSpace(draft.Title))
            return;

        var now = DateTime.UtcNow;
        var document = new ChecklistDocument
        {
            FormatVersion = ChecklistDocument.CurrentFormatVersion,
            Id = Guid.NewGuid(),
            Title = draft.Title.Trim(),
            Description = draft.Description,
            WorkType = draft.WorkType,
            TemplateName = draft.TemplateName,
            TaskDescription = draft.TaskDescription,
            AiPlatform = draft.AiPlatform,
            AiFramework = draft.AiFramework,
            CreatedOn = now,
            UpdatedOn = now,
            IsCompleted = false,
            Items = draft.Items
        };

        TrackCreated(document);
        OpenDocumentTab(document, filePath: null, isDirty: true);
    }

    private async Task<bool> EnsureLocalAiAsync(Window owner, CancellationToken cancellationToken = default)
    {
        if (_aiSetupBusy)
            return false;

        _aiSetupBusy = true;
        try
        {
            AIReadiness readiness;
            try
            {
                readiness = await _aiSetup.CheckAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
                return false;

            if (readiness.IsReady)
                return true;

            var setup = new AiSetupWindow(_aiSetup, readiness.Need);
            return await setup.ShowDialog<bool>(owner) == true;
        }
        finally
        {
            _aiSetupBusy = false;
        }
    }

    private async Task<bool> CreateDailyTrackerAsync()
    {
        var today = DateTime.Today;
        var path = await PickSavePathAsync(SuggestedFileName(DailyTitle(today)));
        if (path is null)
            return false;

        var alreadyOpen = FindTabByPath(path);
        if (alreadyOpen is not null)
        {
            ActivateTab(alreadyOpen);
            return true;
        }

        try
        {
            if (File.Exists(path))
            {
                try
                {
                    var existing = await _fileService.LoadAsync(path);
                    if (existing.WorkType == WorkType.Daily &&
                        existing.Title == DailyTitle(today))
                    {
                        RememberChecklist(path, existing);
                        OpenDocumentTab(existing, path, isDirty: false);
                        return true;
                    }
                }
                catch (ChecklistFileException)
                {
                }
            }

            var now = DateTime.UtcNow;
            var document = new ChecklistDocument
            {
                FormatVersion = ChecklistDocument.CurrentFormatVersion,
                Id = Guid.NewGuid(),
                Title = DailyTitle(today),
                BranchName = string.Empty,
                PullRequestUrl = string.Empty,
                WorkType = WorkType.Daily,
                Deadline = DateOnly.FromDateTime(today),
                CreatedOn = now,
                UpdatedOn = now,
                IsCompleted = false,
                Items = _checklistService.GetTemplate(WorkType.Daily)
            };

            var replaceDifferentDocument = await PrepareOverwriteAsync(path, document.Id);
            if (replaceDifferentDocument is null)
                return false;

            var replacedId = await ReplacedDocumentIdAsync(path, document.Id, replaceDifferentDocument.Value);
            await _fileService.SaveAsync(path, document, replaceDifferentDocument.Value);
            if (replacedId is Guid oldDailyId)
                TrackDeleted(oldDailyId, null);
            TrackCreated(document, path);
            RememberChecklist(path, document, trackActivity: true);
            OpenDocumentTab(document, path, isDirty: false);
            return true;
        }
        catch (Exception ex) when (ex is ChecklistFileException or IOException or UnauthorizedAccessException)
        {
            await ShowMessage(ex is ChecklistFileException fileEx
                ? fileEx.Message
                : "Today's tracker could not be created.");
            return false;
        }
    }

    private static string DailyTitle(DateTime day) =>
        day.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    private void ClearCreateForm()
    {
        WorkTitleTextBox.Text = string.Empty;
        _selectedTemplateId = null;
        ClearTemplateSelection();
    }

    private void OpenDocumentTab(
        ChecklistDocument document,
        string? filePath,
        bool isDirty,
        Guid? openedFromProjectId = null)
    {
        if (!isDirty)
            TrackObserved(document, filePath);

        var view = new ChecklistView(
            document.WorkType,
            document.TemplateName,
            document.Title,
            document.BranchName ?? string.Empty,
            document.PullRequestUrl ?? string.Empty,
            document.Deadline,
            document.DeadlineMinute,
            document.NotifyBeforeMinutes,
            document.DeadlineSilent,
            document.ReminderOn,
            document.ReminderEveryMinutes,
            document.ReminderSilent,
            document.Items);
        view.SetDescription(document.Description);
        view.SetAiContext(document.TaskDescription, document.AiPlatform, document.AiFramework);
        view.EnsureAiReady = EnsureLocalAiAsync;
        view.GenerateOneCheck = (request, token) => _localAi.GenerateCheckAsync(request, token);
        view.ReviewChecklist = (request, token) => _reviews.ReviewAsync(request, token);
        view.ExplainTask = (request, token) => _explanations.ExplainAsync(request, token);

        // Title sizes the tab. It ellipsizes at the width left inside the tab max
        // (190) after horizontal padding (16), the pin button (18), the gaps (12),
        // the close button (24), and the completion mark when it is showing.
        var titleText = new TextBlock
        {
            Text = document.Title,
            MaxWidth = 96,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };

        var completionMark = new Viewbox
        {
            Width = 12,
            Height = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsVisible = false,
            Child = new Avalonia.Controls.Shapes.Path
            {
                Stroke = AppearanceService.Brush("SuccessTextBrush"),
                StrokeThickness = 1.8,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Data = Geometry.Parse("M1,5.5 L4.2,8.5 L10,2")
            }
        };

        ToolTip.SetTip(completionMark, "Completed");

        var projectLabel = new TextBlock
        {
            MaxWidth = 96,
            FontSize = 10,
            LineHeight = 12,
            Margin = new Avalonia.Thickness(0),
            Padding = new Avalonia.Thickness(0),
            TextAlignment = TextAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            IsVisible = false
        };

        var titleStack = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Spacing = 0,
            Margin = new Avalonia.Thickness(0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children = { titleText, projectLabel }
        };

        var titleRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children = { completionMark, titleStack }
        };

        var pinButton = new Button
        {
            Content = CreatePinIcon(),
            Classes = { "tab-pin" }
        };
        ToolTip.SetTip(pinButton, "Pin tab");

        var closeButton = new Button
        {
            Content = "×"
        };

        closeButton.Classes.Add("tab-close");
        ToolTip.SetTip(closeButton, AppShortcuts.WithShortcut(AppShortcuts.CloseTab));
        closeButton.AddHandler(
            PointerPressedEvent,
            (_, e) => e.Handled = true,
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"),
            ColumnSpacing = 6,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        Grid.SetColumn(pinButton, 0);
        Grid.SetColumn(titleRow, 1);
        Grid.SetColumn(closeButton, 2);

        headerGrid.Children.Add(pinButton);
        headerGrid.Children.Add(titleRow);
        headerGrid.Children.Add(closeButton);

        var tabHeader = new Border
        {
            Classes = { "workspace-tab" },
            Height = 40,
            Padding = new Avalonia.Thickness(8, 3),
            Child = headerGrid
        };

        var tab = new ChecklistTab(document, view, tabHeader, titleText, projectLabel, completionMark, pinButton)
        {
            FilePath = filePath,
            IsDirty = isDirty,
            OpenedFromProjectId = openedFromProjectId
        };

        view.SetManualSaveVisible(string.IsNullOrWhiteSpace(filePath));
        view.SetDocumentCompleted(document.IsCompleted);
        view.DocumentEdited += (_, _) => OnDocumentEdited(tab);
        view.DocumentCompleted += (_, _) => OnDocumentCompleted(tab);
        view.DocumentReopened += (_, _) => OnDocumentReopened(tab);
        view.SaveRequested += (_, _) => _ = RunFileOperation(() => SaveAsync(tab));
        view.SaveAsTemplateRequested += (_, _) => _ = SaveTabAsTemplateAsync(tab);
        view.CopyRequested += (_, _) => _ = RunFileOperation(() => CopyChecklistAsync(tab));
        view.ExportJsonRequested += (_, _) => _ = RunFileOperation(() => SaveAsAsync(tab));
        view.ExportCsvRequested += (_, _) => _ = RunFileOperation(() => ExportCsvAsync(tab));
        view.CopyTextRequested += (_, _) => _ = CopyChecklistTextAsync(tab);
        view.SubtasksRequired += (_, message) => ShowToast(message);
        view.LinkedSubtaskOpenRequested += (_, subtask) => _ = OpenLinkedSubtaskAsync(subtask);
        view.CopiedFromOpenRequested += (_, path) => _ = RunFileOperation(() => OpenCopiedFromAsync(path));
        view.RequestLink = owner => PickLinkedChecklistAsync(tab, owner);

        tabHeader.PointerPressed += (_, e) => BeginTabDrag(tab, e);
        tabHeader.PointerMoved += (_, e) => UpdateTabDrag(tab, e);
        tabHeader.PointerReleased += (_, e) => EndTabDrag(tab, e);
        tabHeader.PointerCaptureLost += (_, _) => EndTabDrag(tab, null);

        closeButton.Click += (_, e) =>
        {
            e.Handled = true;
            _ = CloseTabFromUserAsync(tab);
        };
        pinButton.Click += (_, _) => TogglePin(tab);

        _tabs.Add(tab);
        if (openedFromProjectId is Guid parentProjectId)
        {
            var parent = _projectTabs.FirstOrDefault(item => item.Project.Id == parentProjectId);
            if (parent is not null)
                parent.ChildrenCollapsed = false;
        }

        if (StripNeedsGroups())
            ArrangeTabStrip();
        else
            TabsPanel.Children.Add(tabHeader);
        UpdatePinVisibility();

        UpdateTabChrome(tab);
        SyncLinkedChecklists(tab);
        ActivateTab(tab);
        SyncReminders();
    }

    private static Control CreatePinIcon()
    {
        return new Panel
        {
            Width = 12,
            Height = 12,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children =
            {
                new Viewbox
                {
                    Width = 12,
                    Height = 12,
                    Stretch = Stretch.Uniform,
                    Classes = { "pin-off" },
                    Child = PinGlyph(filled: false)
                },
                new Viewbox
                {
                    Width = 12,
                    Height = 12,
                    Stretch = Stretch.Uniform,
                    Classes = { "pin-on" },
                    Child = PinGlyph(filled: true)
                }
            }
        };
    }

    private static Avalonia.Controls.Shapes.Path PinGlyph(bool filled)
    {
        // Angled pushpin in a 16×16 box. Off is the outline; on is the solid pin.
        const string outline =
            "M10,3 L7.3,5.7 L4.3,6.7 L3.3,7.7 L8,12.3 L9,11.3 L10,8.3 L12.7,5.7 " +
            "M6,10 L3,13 M9.7,2.7 L13.3,6.3";
        const string solid =
            "M10,3 L7.3,5.7 L4.3,6.7 L3.3,7.7 L8,12.3 L9,11.3 L10,8.3 L12.7,5.7 Z " +
            "M6.6,9.3 L2.8,13.1 L2.1,12.4 L5.9,8.6 Z " +
            "M9.3,2.2 L13.8,6.7 L13.1,7.4 L8.6,2.9 Z";

        return new Avalonia.Controls.Shapes.Path
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            StrokeThickness = filled ? 0 : 1.6,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stroke = AppearanceService.Brush(filled ? AppearanceService.TextOnAccentBrush : AppearanceService.TextPrimaryBrush),
            Fill = filled ? AppearanceService.Brush(AppearanceService.TextOnAccentBrush) : Brushes.Transparent,
            Data = Geometry.Parse(filled ? solid : outline)
        };
    }

    private async Task CloseTabAsync(ChecklistTab tab)
    {
        if (_tabs.IndexOf(tab) < 0)
            return;

        tab.AutoSaveTimer?.Stop();

        if (!string.IsNullOrWhiteSpace(tab.FilePath))
        {
            if (tab.IsDirty && !await WriteDocumentAsync(tab, tab.FilePath, announce: false))
                return;
        }
        else if (tab.IsDirty)
        {
            var choice = await PromptUnsavedAsync(tab.Document.Title);
            if (choice == ClosePromptResult.Cancel)
                return;

            if (choice == ClosePromptResult.Save && !await SaveAsync(tab))
                return;
        }

        RemoveTab(tab);
    }

    private async Task CloseTabFromUserAsync(ChecklistTab tab)
    {
        tab.AutoSaveTimer?.Stop();
        while (tab.AutoSaveTask is { } pending)
            await pending;

        await RunFileOperation(() => CloseTabAsync(tab));
    }

    private void RemoveTab(ChecklistTab tab)
    {
        if (!_tabs.Contains(tab))
            return;

        var wasActive = _activeTab == tab;
        var before = CaptureTabs();
        var projectIds = CaptureProjectIds();
        DetachChecklist(tab);
        UpdatePinVisibility();
        if (StripNeedsGroups())
            ArrangeTabStrip();

        if (!wasActive)
        {
            TabBar.IsVisible = true;
            RefreshRecentList();
            RefreshNotificationBell();
            RefreshProjectSurfaces();
            return;
        }

        ApplyFocus(TabSession.AfterChecklistClosed(
            before,
            projectIds,
            new TabFocus(tab.Document.Id, null),
            tab.Document.Id));
        RefreshRecentList();
        RefreshNotificationBell();
        RefreshProjectSurfaces();
    }

    private void BeginTabDrag(ChecklistTab tab, PointerPressedEventArgs e)
    {
        if (IsInsideButton(e.Source))
            return;

        if (!e.GetCurrentPoint(tab.HeaderContainer).Properties.IsLeftButtonPressed)
            return;

        ActivateTab(tab);
        _dragCanReorder = tab.OpenedFromProjectId is null
            && !tab.IsPinned
            && _tabs.Count(open => open.OpenedFromProjectId is null && !open.IsPinned) >= 2;
        if (!_dragCanReorder && _projectTabs.Count == 0)
            return;

        _dragTab = tab;
        _dragStart = e.GetPosition(TabsPanel);
        var shift = (tab.HeaderContainer.RenderTransform as TranslateTransform)?.X ?? 0;
        _dragOriginX = StripX(tab.HeaderContainer) - shift;
        _dragTarget = tab.OpenedFromProjectId is Guid projectId
            ? _tabs.Where(open => open.OpenedFromProjectId == projectId).ToList().IndexOf(tab)
            : !_dragCanReorder
                ? -1
                : StripNeedsGroups()
                    ? _tabs.Where(open => open.OpenedFromProjectId is null && !open.IsPinned).ToList().IndexOf(tab)
                    : _tabs.IndexOf(tab);
        _dragProject = null;
        _dragReleaseChild = false;
        _dragPointerX = _dragStart.X;
        _tabDragging = false;
        e.Pointer.Capture(tab.HeaderContainer);
        e.Handled = true;
    }

    private void UpdateTabDrag(ChecklistTab tab, PointerEventArgs e)
    {
        if (_dragTab != tab)
            return;

        if (!e.GetCurrentPoint(tab.HeaderContainer).Properties.IsLeftButtonPressed)
        {
            EndTabDrag(tab, e);
            return;
        }

        var position = e.GetPosition(TabsPanel);
        if (!_tabDragging)
        {
            if (Math.Abs(position.X - _dragStart.X) < 6)
                return;

            _tabDragging = true;
            tab.HeaderContainer.Classes.Add("dragging");
            tab.HeaderContainer.ZIndex = 2;
            _dragLayer = ProjectGroupOf(tab.HeaderContainer);
            if (_dragLayer is not null)
                _dragLayer.ZIndex = 2;
            tab.HeaderContainer.Cursor = new Cursor(StandardCursorType.SizeWestEast);
        }

        _dragPointerX = position.X;
        var home = HomeProject(tab);
        var project = ProjectUnderDrag(position, tab);
        if (project is not null && home is not null && project.Project.Id == home.Project.Id)
            project = null;

        SetProjectDropTarget(project);
        var insideHome = home is not null && PointerInsideProject(home, position);
        _dragReleaseChild = home is not null && project is null && !insideHome;
        tab.HeaderContainer.Cursor = new Cursor(project is not null
            ? StandardCursorType.DragCopy
            : _dragReleaseChild
                ? StandardCursorType.DragMove
                : StandardCursorType.SizeWestEast);

        var grab = _dragStart.X - _dragOriginX;
        var visualX = ClampDraggedVisualX(tab, position.X - grab);
        if (project is not null || _dragReleaseChild)
        {
            _dragTarget = -1;
            ResetTabSlidesExcept(tab);
        }
        else if (insideHome)
        {
            if (SiblingCount(tab) >= 2)
            {
                var target = DropIndexAmongChildren(position.X, tab);
                if (target != _dragTarget)
                {
                    _dragTarget = target;
                    SlideChildTabs(tab, target);
                }
            }
            else
            {
                _dragTarget = 0;
                ResetTabSlidesExcept(tab);
            }
        }
        else if (_dragCanReorder)
        {
            var target = DropIndex(position.X, tab);
            if (target != _dragTarget)
            {
                _dragTarget = target;
                SlideTabsForDrop(tab, target);
            }
        }

        PlaceDraggedTab(tab, visualX);
        e.Handled = true;
    }

    private void EndTabDrag(ChecklistTab tab, PointerEventArgs? e)
    {
        if (_dragTab != tab)
            return;

        var dropped = _tabDragging;
        var target = _dragTarget;
        var project = _dragProject;
        var releaseChild = _dragReleaseChild;
        var pointerX = _dragPointerX;
        _dragTab = null;
        _tabDragging = false;
        _dragTarget = -1;
        _dragCanReorder = false;
        _dragReleaseChild = false;
        SetProjectDropTarget(null);
        tab.HeaderContainer.Classes.Remove("dragging");
        tab.HeaderContainer.ClearValue(Avalonia.Input.InputElement.CursorProperty);
        if (_dragLayer is not null)
        {
            _dragLayer.ZIndex = 0;
            _dragLayer = null;
        }

        if (e is not null)
            e.Pointer.Capture(null);

        var foreignProject = project is not null &&
            (tab.OpenedFromProjectId is not Guid parent || project.Project.Id != parent);
        if (dropped && foreignProject)
        {
            ClearTabSlides();
            _ = RunFileOperation(() => AdoptTabIntoProjectAsync(project!.Project, tab));
        }
        else if (dropped && releaseChild)
            ReleaseChildToStandalone(tab, pointerX);
        else if (dropped && tab.OpenedFromProjectId is not null)
            CommitChildDrop(tab, target);
        else if (dropped && target >= 0)
            CommitTabDrop(tab, target);
        else
            ClearTabSlides();
    }

    /// <summary>
    /// Index after the dragged tab is removed. Pinned tabs stay in front, so the slot
    /// starts at the first unpinned tab. Centers use layout positions so the choice
    /// does not flicker while tabs slide.
    /// </summary>
    private int DropIndex(double pointerX, ChecklistTab dragging)
    {
        if (StripNeedsGroups())
            return DropIndexAmongStandalones(pointerX, dragging);

        var pinned = _tabs.Count(tab => tab.IsPinned);
        var index = pinned;
        foreach (var other in _tabs)
        {
            if (other == dragging || other.IsPinned)
                continue;

            var bounds = other.HeaderContainer.Bounds;
            if (pointerX > bounds.X + bounds.Width / 2)
                index++;
        }

        return index;
    }

    private void SlideTabsForDrop(ChecklistTab dragging, int target)
    {
        if (StripNeedsGroups())
        {
            SlideStandaloneTabs(dragging, target);
            return;
        }

        var from = _tabs.IndexOf(dragging);
        if (from < 0)
            return;

        var pitch = dragging.HeaderContainer.Bounds.Width + TabsPanel.Spacing;
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (i == from || _tabs[i].IsPinned)
                continue;

            var offset = 0d;
            if (target > from && i > from && i <= target)
                offset = -pitch;
            else if (target < from && i >= target && i < from)
                offset = pitch;

            AnimateTabSlide(_tabs[i].HeaderContainer, offset);
        }
    }

    /// <summary>
    /// Keeps the dragged tab inside the visible tab strip, including when the pointer leaves the window.
    /// </summary>
    private double ClampDraggedVisualX(ChecklistTab tab, double visualX)
    {
        var width = tab.HeaderContainer.Bounds.Width;
        var left = TabsScrollViewer.Offset.X;
        var pinEdge = PinnedBoundaryX();
        if (visualX < pinEdge)
            visualX = pinEdge;
        var view = TabsScrollViewer.Viewport.Width;
        if (view <= 0 || width <= 0)
            return visualX;

        var right = left + view - width;
        if (right < left)
            right = left;

        if (visualX < left)
            return left;
        if (visualX > right)
            return right;

        return visualX;
    }

    private void PlaceDraggedTab(ChecklistTab tab, double visualX)
    {
        var header = tab.HeaderContainer;
        var translate = EnsureTabTranslate(header);
        translate.X = visualX - _dragOriginX;
        translate.Y = 0;
    }

    private ProjectTab? HomeProject(ChecklistTab tab)
    {
        if (tab.OpenedFromProjectId is not Guid projectId)
            return null;

        return _projectTabs.FirstOrDefault(project => project.Project.Id == projectId);
    }

    private int SiblingCount(ChecklistTab tab) =>
        tab.OpenedFromProjectId is not Guid projectId
            ? 0
            : _tabs.Count(item => item.OpenedFromProjectId == projectId);

    private bool PointerInsideProject(ProjectTab project, Point pointer)
    {
        var origin = project.Group.TranslatePoint(new Point(0, 0), TabsPanel);
        if (origin is not Point start)
            return false;

        var bounds = new Rect(start, project.Group.Bounds.Size).Inflate(6);
        return bounds.Contains(pointer);
    }

    private double LayoutStripX(Control control)
    {
        var shift = (control.RenderTransform as TranslateTransform)?.X ?? 0;
        return StripX(control) - shift;
    }

    private double StripX(Control control)
    {
        var point = control.TranslatePoint(new Point(0, 0), TabsPanel);
        return point?.X ?? control.Bounds.X;
    }

    private ProjectTab? ProjectUnderDrag(Point pointer, ChecklistTab tab)
    {
        if (HitProject(pointer) is { } direct)
            return direct;

        var header = tab.HeaderContainer;
        var center = header.TranslatePoint(
            new Point(header.Bounds.Width / 2, header.Bounds.Height / 2),
            TabsPanel);
        return center is Point point ? HitProject(point) : null;
    }

    private ProjectTab? HitProject(Point point)
    {
        foreach (var project in _projectTabs)
        {
            var origin = project.Header.TranslatePoint(new Point(0, 0), TabsPanel);
            if (origin is not Point start)
                continue;

            var bounds = new Rect(start, project.Header.Bounds.Size).Inflate(8);
            if (bounds.Contains(point))
                return project;
        }

        return null;
    }

    private void SetProjectDropTarget(ProjectTab? project)
    {
        if (_dragProject == project)
            return;

        _dragProject?.Header.Classes.Remove("drop-target");
        _dragProject?.Group.Classes.Remove("drop-target");
        _dragProject = project;
        project?.Header.Classes.Add("drop-target");
        project?.Group.Classes.Add("drop-target");
    }

    private static Border? ProjectGroupOf(Control control)
    {
        var current = control.Parent;
        while (current is not null)
        {
            if (current is Border border && border.Classes.Contains("project-group"))
                return border;
            current = current.Parent;
        }

        return null;
    }

    private void ResetTabSlidesExcept(ChecklistTab dragging)
    {
        foreach (var open in _tabs)
        {
            if (open == dragging)
                continue;

            var shift = (open.HeaderContainer.RenderTransform as TranslateTransform)?.X ?? 0;
            if (Math.Abs(shift) > 0.5 || _slideTarget.ContainsKey(open.HeaderContainer))
                AnimateTabSlide(open.HeaderContainer, 0);
        }
    }

    private void CommitTabDrop(ChecklistTab dragging, int target)
    {
        if (StripNeedsGroups())
        {
            CommitStandaloneDrop(dragging, target);
            return;
        }

        CancelTabSlides();

        var visualX = new Dictionary<ChecklistTab, double>();
        foreach (var open in _tabs)
        {
            var shift = (open.HeaderContainer.RenderTransform as TranslateTransform)?.X ?? 0;
            visualX[open] = open.HeaderContainer.Bounds.X + shift;
        }

        var from = _tabs.IndexOf(dragging);
        var min = _tabs.Count(tab => tab.IsPinned);
        if (target < min)
            target = min;
        if (from >= 0 && target >= 0 && target != from)
        {
            _tabs.RemoveAt(from);
            _tabs.Insert(target, dragging);
            TabsPanel.Children.RemoveAt(from);
            TabsPanel.Children.Insert(target, dragging.HeaderContainer);
            TabsPanel.UpdateLayout();
        }

        foreach (var open in _tabs)
        {
            var header = open.HeaderContainer;
            var translate = EnsureTabTranslate(header);
            translate.Y = 0;
            translate.X = visualX[open] - header.Bounds.X;
            AnimateTabSlide(header, 0);
        }
    }

    private void ClearTabSlides()
    {
        CancelTabSlides();
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

    private static TranslateTransform EnsureTabTranslate(Control header)
    {
        if (header.RenderTransform is TranslateTransform translate)
            return translate;

        translate = new TranslateTransform();
        header.RenderTransform = translate;
        return translate;
    }

    private void AnimateTabSlide(Border header, double to)
    {
        EnsureTabTranslate(header);
        _slideTarget[header] = to;
        if (_slideClock)
            return;

        _slideClock = true;
        var epoch = _slideEpoch;
        Dispatcher.UIThread.Post(() => StepTabSlides(epoch), DispatcherPriority.Render);
    }

    private void StepTabSlides(int epoch)
    {
        if (epoch != _slideEpoch)
            return;

        var moving = false;
        foreach (var pair in _slideTarget.ToArray())
        {
            var header = pair.Key;
            if (header.RenderTransform is not TranslateTransform translate)
                continue;

            var to = pair.Value;
            var next = translate.X + (to - translate.X) * 0.42;
            if (Math.Abs(to - next) < 0.5)
            {
                translate.X = to;
                _slideTarget.Remove(header);
                if (Math.Abs(to) < 0.5)
                    header.ZIndex = 0;
            }
            else
            {
                translate.X = next;
                moving = true;
            }
        }

        if (epoch != _slideEpoch)
            return;

        if (moving)
            Dispatcher.UIThread.Post(() => StepTabSlides(epoch), DispatcherPriority.Render);
        else
            _slideClock = false;
    }

    private void UpdatePinVisibility()
    {
        var show = _tabs.Count(tab => tab.OpenedFromProjectId is null) > 1;
        foreach (var tab in _tabs)
            tab.PinButton.IsVisible = show && tab.OpenedFromProjectId is null;

        UpdatePinMenuItem();
    }

    private double PinnedBoundaryX()
    {
        double edge = double.NegativeInfinity;
        foreach (var tab in _tabs)
        {
            if (!tab.IsPinned)
                break;

            var bounds = tab.HeaderContainer.Bounds;
            edge = bounds.X + bounds.Width + TabsPanel.Spacing;
        }

        return edge;
    }

    private void TogglePin(ChecklistTab tab)
    {
        if (tab.OpenedFromProjectId is not null)
            return;

        if (_dragTab == tab)
            EndTabDrag(tab, null);

        tab.IsPinned = !tab.IsPinned;
        if (tab.IsPinned)
        {
            tab.PinButton.Classes.Add("pinned");
            ToolTip.SetTip(tab.PinButton, "Unpin tab");
        }
        else
        {
            tab.PinButton.Classes.Remove("pinned");
            ToolTip.SetTip(tab.PinButton, "Pin tab");
        }

        if (StripNeedsGroups())
        {
            var pinned = _tabs.Where(open => open.OpenedFromProjectId is null && open.IsPinned).ToList();
            var loose = _tabs.Where(open => open.OpenedFromProjectId is null && !open.IsPinned).ToList();
            var children = _tabs.Where(open => open.OpenedFromProjectId is not null).ToList();
            _tabs.Clear();
            _tabs.AddRange(pinned);
            _tabs.AddRange(loose);
            _tabs.AddRange(children);
            ArrangeTabStrip();
            ActivateTab(tab);
            SetChecklistActionsEnabled(true);
            return;
        }

        var from = _tabs.IndexOf(tab);
        if (from < 0)
            return;

        _tabs.RemoveAt(from);
        var index = tab.IsPinned
            ? _tabs.Count(open => open.IsPinned)
            : _tabs.FindIndex(open => !open.IsPinned);
        if (index < 0)
            index = _tabs.Count;

        _tabs.Insert(index, tab);
        TabsPanel.Children.RemoveAt(from);
        TabsPanel.Children.Insert(index, tab.HeaderContainer);
        ActivateTab(tab);
        SetChecklistActionsEnabled(true);
    }

    private void CancelTabSlides()
    {
        _slideEpoch++;
        _slideTarget.Clear();
        _slideClock = false;
    }

    private static bool IsInsideButton(object? source)
    {
        for (var node = source as Avalonia.StyledElement; node is not null; node = node.Parent)
        {
            if (node is Button)
                return true;
        }

        return false;
    }

    private void ActivateTab(ChecklistTab tab)
    {
        _returnToHistory = false;
        HideHistory();
        _activeTab = tab;
        _activeProjectTab = null;

        ClearChecklistSelection();
        ClearProjectSelection();

        tab.HeaderContainer.Classes.Add("selected");

        _settingsOpen = false;
        _shortcutsOpen = false;
        _settingsOverChecklist = false;
        _settingsOverReminders = false;
        _remindersOpen = false;
        RemindersButton.Classes.Remove("selected");
        RemindersPanel.IsVisible = false;
        SettingsPanel.IsVisible = false;
        ShortcutsPanel.IsVisible = false;
        WorkspaceContent.Content = tab.View;
        WorkspaceContent.IsVisible = true;
        TabBar.IsVisible = true;
        HomePanel.IsVisible = false;
        SetChecklistActionsEnabled(true);
        UpdateManualSaveVisibility();
        SyncProjectIndicators();
        SyncHomeTab();
    }

    private void HomeTab_Click(object? sender, RoutedEventArgs e) =>
        ShowHomeScreen();

    private void HomeSettings_Click(object? sender, RoutedEventArgs e) =>
        InvokeShortcut(AppShortcuts.Settings);

    private void CreateTasklist_Click(object? sender, RoutedEventArgs e)
    {
        NewTabButton.Flyout?.Hide();
        ShowCreateChecklist();
    }

    private void ViewAllProjects_Click(object? sender, RoutedEventArgs e)
    {
        ShowHomeScreen();
        ShowHomeSurface(HomeSurface.Projects);
    }

    private void ViewAllFiles_Click(object? sender, RoutedEventArgs e)
    {
        ShowHomeScreen();
        ShowHomeSurface(HomeSurface.Files);
    }

    private void HomeLayout_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (ActionRow.ColumnDefinitions.Count < 2)
            return;

        var narrow = e.NewSize.Width < 680;
        ActionRow.ColumnDefinitions[1].Width = narrow
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(CreateTasklistButton, narrow ? 0 : 1);
        Grid.SetRow(CreateTasklistButton, narrow ? 1 : 0);
        LayoutTemplateWrap(e.NewSize.Width);
    }

    private void LayoutTemplateWrap(double available)
    {
        const double tile = 158;
        available = Math.Min(available, 1040);
        if (available < tile)
        {
            TemplateList.Width = double.NaN;
            return;
        }

        var columns = Math.Max(1, (int)Math.Floor(available / tile));
        TemplateList.Width = columns * tile;
    }

    private enum HomeSurface
    {
        Dashboard,
        Create,
        CreateProject,
        Projects,
        Files
    }

    private void ShowHomeScreen()
    {
        if (_surfaceDepth > 0)
            return;

        _returnToHistory = false;
        HideHistory();
        _settingsOpen = false;
        _shortcutsOpen = false;
        _settingsOverChecklist = false;
        _settingsOverReminders = false;
        _remindersOpen = false;
        RemindersButton.Classes.Remove("selected");
        SettingsPanel.IsVisible = false;
        ShortcutsPanel.IsVisible = false;
        RemindersPanel.IsVisible = false;
        WorkspaceContent.IsVisible = false;
        WorkspaceContent.Content = null;

        HomePanel.IsVisible = true;
        TabBar.IsVisible = true;
        ClearChecklistSelection();
        ClearProjectSelection();
        _activeProjectTab = null;
        SetChecklistActionsEnabled(false);
        UpdateManualSaveVisibility();
        RefreshRecentList();
        RefreshProjectOverview();
        ShowHomeSurface(HomeSurface.Dashboard);
        SyncProjectIndicators();
    }

    private void ShowCreateChecklist()
    {
        ShowHomeScreen();
        ShowHomeSurface(HomeSurface.Create);
    }

    private void ShowHomeSurface(HomeSurface surface)
    {
        HomeDashboard.IsVisible = surface == HomeSurface.Dashboard;
        CreateChecklistPanel.IsVisible = surface == HomeSurface.Create;
        CreateProjectPanel.IsVisible = surface == HomeSurface.CreateProject;
        AllProjectsPanel.IsVisible = surface == HomeSurface.Projects;
        AllFilesPanel.IsVisible = surface == HomeSurface.Files;
        if (surface == HomeSurface.Create)
            LayoutTemplateWrap(HomeLayout.Bounds.Width);
        if (surface == HomeSurface.Projects)
            RefreshProjectOverview();
        if (surface == HomeSurface.Files)
            RefreshAllFiles();
        SyncHomeTab();
    }

    private HomeSurface CurrentHomeSurface()
    {
        if (CreateProjectPanel.IsVisible)
            return HomeSurface.CreateProject;
        if (CreateChecklistPanel.IsVisible)
            return HomeSurface.Create;
        if (AllProjectsPanel.IsVisible)
            return HomeSurface.Projects;
        if (AllFilesPanel.IsVisible)
            return HomeSurface.Files;
        return HomeSurface.Dashboard;
    }

    private void SyncHomeTab()
    {
        var onDashboard = HomePanel.IsVisible && HomeDashboard.IsVisible;
        if (onDashboard)
        {
            if (!HomeTabButton.Classes.Contains("selected"))
                HomeTabButton.Classes.Add("selected");
        }
        else
        {
            HomeTabButton.Classes.Remove("selected");
        }
    }

    private void SetChecklistActionsEnabled(bool enabled)
    {
        var editable = enabled && ActiveTab()?.Document.IsCompleted != true;
        if (_addCheckMenuItem is not null)
            _addCheckMenuItem.IsEnabled = editable;

        WindowAddCheckMenuItem.IsEnabled = editable;

        var hasItems = enabled && ActiveTab()?.View.Items.Count > 0;
        var canRearrange = editable && ActiveTab()?.View.Items.Count > 1;
        SetItemActionVisible(_exportJsonMenuItem, WindowExportJsonMenuItem, enabled);
        SetItemActionVisible(_exportCsvMenuItem, WindowExportCsvMenuItem, enabled);
        SetItemActionVisible(_copyTextMenuItem, WindowCopyTextMenuItem, enabled);
        SetItemActionVisible(_copyToMenuItem, WindowCopyMenuItem, editable && hasItems);
        SetItemActionVisible(_deleteChecksMenuItem, WindowDeleteMenuItem, editable && hasItems);
        SetItemActionVisible(_saveTemplateMenuItem, WindowSaveTemplateMenuItem, editable);
        SetItemActionVisible(_rearrangeMenuItem, WindowRearrangeMenuItem, canRearrange);
        UpdatePinMenuItem();
    }

    private void UpdatePinMenuItem()
    {
        var checklistOpen = !HomePanel.IsVisible && !_historyOpen && !SettingsPanel.IsVisible && !ShortcutsPanel.IsVisible && !RemindersPanel.IsVisible && ActiveTab() is not null;
        var pinLabel = ActiveTab()?.IsPinned == true ? "Unpin Tab" : "Pin Tab";
        var standaloneTabs = _tabs.Count(tab => tab.OpenedFromProjectId is null);
        if (_pinTabMenuItem is not null)
            _pinTabMenuItem.Header = pinLabel;
        WindowPinMenuItem.Header = pinLabel;
        SetItemActionVisible(
            _pinTabMenuItem,
            WindowPinMenuItem,
            checklistOpen && ActiveTab()?.OpenedFromProjectId is null && standaloneTabs > 1);
    }

    private static void SetItemActionVisible(NativeMenuItem? nativeItem, MenuItem windowItem, bool visible)
    {
        if (nativeItem is not null)
        {
            nativeItem.IsVisible = visible;
            nativeItem.IsEnabled = visible;
        }

        windowItem.IsVisible = visible;
        windowItem.IsEnabled = visible;
    }

    private void ConfigureTitleChrome()
    {
        if (!OperatingSystem.IsWindows())
            return;

        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = TitleChrome.Height;
        WindowMenu.IsVisible = true;
        NativeMenu.SetMenu(this, null);
        WindowDecorationProperties.SetElementRole(TitleChrome, WindowDecorationsElementRole.TitleBar);
        WindowDecorationProperties.SetElementRole(WindowMenu, WindowDecorationsElementRole.User);
        WindowDecorationProperties.SetElementRole(RemindersButton, WindowDecorationsElementRole.User);
        WindowDecorationProperties.SetElementRole(ReminderBell, WindowDecorationsElementRole.User);
        ApplyWindowMenuGestures();
        FitWindowsTitlePadding();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == OffScreenMarginProperty)
                FitWindowsTitlePadding();
        };
    }

    private void FitWindowsTitlePadding()
    {
        var off = OffScreenMargin;
        // Minimize and close. Title text, maximize, and full screen are hidden.
        const double captionButtons = 46 * 2 + 12;
        TitleChrome.Padding = new Avalonia.Thickness(12 + off.Left, off.Top, captionButtons + off.Right, 0);
        TitleChrome.Height = 40 + off.Top;
        ExtendClientAreaTitleBarHeightHint = TitleChrome.Height;
    }

    private void ApplyWindowMenuGestures()
    {
        foreach (var top in WindowMenu.Items.OfType<MenuItem>())
        {
            foreach (var item in top.Items.OfType<MenuItem>())
            {
                var header = item.Header as string;
                var id = header switch
                {
                    "New Checklist" => AppShortcuts.NewChecklist,
                    "Create Project" => AppShortcuts.CreateProject,
                    "Open…" => AppShortcuts.OpenChecklist,
                    "Save" => AppShortcuts.Save,
                    "Add Check" => AppShortcuts.AddItem,
                    "Save as template" => AppShortcuts.SaveAsTemplate,
                    "Rearrange…" => AppShortcuts.Rearrange,
                    "Settings" => AppShortcuts.Settings,
                    _ => null
                };

                item.Click += (_, _) => InvokeWindowMenu(header);

                if (id is not null)
                    item.InputGesture = AppShortcuts.All.First(entry => entry.Id == id).MenuGesture;
            }
        }
    }

    private void InvokeWindowMenu(string? header)
    {
        switch (header)
        {
            case "New Checklist":
                InvokeShortcut(AppShortcuts.NewChecklist);
                break;
            case "Create Project":
                InvokeShortcut(AppShortcuts.CreateProject);
                break;
            case "Open…":
                InvokeShortcut(AppShortcuts.OpenChecklist);
                break;
            case "Save":
                InvokeShortcut(AppShortcuts.Save);
                break;
            case "Save As…":
                InvokeShortcut(AppShortcuts.SaveAs);
                break;
            case "Export as JSON…":
                ExportJsonMenu_Click(this, EventArgs.Empty);
                break;
            case "Export as CSV…":
                ExportCsvMenu_Click(this, EventArgs.Empty);
                break;
            case "Copy as Text":
                CopyTextMenu_Click(this, EventArgs.Empty);
                break;
            case "Reminders":
                ShowReminders();
                break;
            case "Add Check":
                InvokeShortcut(AppShortcuts.AddItem);
                break;
            case "Save as template":
                InvokeShortcut(AppShortcuts.SaveAsTemplate);
                break;
            case "Rearrange…":
                InvokeShortcut(AppShortcuts.Rearrange);
                break;
            case "Copy to…":
                CopyMenu_Click(this, EventArgs.Empty);
                break;
            case "Delete…":
                DeleteChecksMenu_Click(this, EventArgs.Empty);
                break;
            case "Pin Tab":
            case "Unpin Tab":
                PinTabMenu_Click(this, EventArgs.Empty);
                break;
            case "Settings":
                InvokeShortcut(AppShortcuts.Settings);
                break;
            case "Keyboard Shortcuts":
                ShowShortcuts();
                break;
            case "Check for Updates…":
                CheckForUpdatesMenu_Click(this, EventArgs.Empty);
                break;
        }
    }

    private void ShowSettings() => ShowOverlay(settings: true);

    private void ShowShortcuts() => ShowOverlay(settings: false);

    private void ShowOverlay(bool settings)
    {
        _surfaceDepth++;
        try
        {
            if (!_settingsOpen && !_shortcutsOpen)
            {
                if (_historyOpen)
                    _returnToHistory = true;
                _settingsOverReminders = _remindersOpen;
                _settingsOverChecklist = !HomePanel.IsVisible
                    && !_historyOpen
                    && !_remindersOpen
                    && (_activeTab is not null || _activeProjectTab is not null);
            }

            _settingsOpen = settings;
            _shortcutsOpen = !settings;
            _remindersOpen = false;
            RemindersButton.Classes.Remove("selected");
            HomePanel.IsVisible = false;
            WorkspaceContent.IsVisible = false;
            RemindersPanel.IsVisible = false;
            SettingsPanel.IsVisible = settings;
            ShortcutsPanel.IsVisible = !settings;
            HideHistory();
            if (settings)
                SyncAppearanceSelection();
            TabBar.IsVisible = true;
            UpdateManualSaveVisibility();
            SyncProjectIndicators();
            SyncHomeTab();
            if (settings)
                KeepSettingsOnTop();
        }
        finally
        {
            _surfaceDepth--;
        }
    }

    private void KeepSettingsOnTop()
    {
        HomePanel.IsVisible = false;
        WorkspaceContent.IsVisible = false;
        RemindersPanel.IsVisible = false;
        ShortcutsPanel.IsVisible = false;
        SettingsPanel.IsVisible = true;
        _settingsOpen = true;
        _shortcutsOpen = false;
        if (_settingsOverChecklist && _activeTab is not null && _tabs.Contains(_activeTab))
        {
            ClearChecklistSelection();
            ClearProjectSelection();
            _activeTab.HeaderContainer.Classes.Add("selected");
        }
        else if (_settingsOverChecklist && _activeProjectTab is not null && _projectTabs.Contains(_activeProjectTab))
        {
            ClearChecklistSelection();
            ClearProjectSelection();
            _activeProjectTab.Header.Classes.Add("selected");
        }

        SyncHomeTab();
    }

    private void CloseSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (_settingsOverReminders)
            ShowReminders();
        else if (_settingsOverChecklist && _activeProjectTab is not null && _projectTabs.Contains(_activeProjectTab))
            ActivateProject(_activeProjectTab);
        else if (_settingsOverChecklist && _activeTab is not null && _tabs.Contains(_activeTab))
            ActivateTab(_activeTab);
        else if (_returnToHistory)
        {
            _returnToHistory = false;
            ShowHistory();
        }
        else
            ShowHomeScreen();
    }

    private void RemindersMenu_Click(object? sender, EventArgs e) =>
        ShowReminders();

    private void RemindersButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_remindersOpen)
            CloseReminders();
        else
            ShowReminders();
    }

    private void CloseReminders_Click(object? sender, RoutedEventArgs e) =>
        CloseReminders();

    private void CloseReminders()
    {
        if (_returnToHistory)
        {
            _returnToHistory = false;
            ShowHistory();
            return;
        }

        if (_activeTab is not null && _tabs.Contains(_activeTab))
            ActivateTab(_activeTab);
        else if (_activeProjectTab is not null && _projectTabs.Contains(_activeProjectTab))
            ActivateProject(_activeProjectTab);
        else
            ShowHomeScreen();
    }

    private void ShowReminders()
    {
        if (_historyOpen)
            _returnToHistory = true;

        _settingsOpen = false;
        _shortcutsOpen = false;
        _settingsOverChecklist = false;
        _settingsOverReminders = false;
        _remindersOpen = true;
        ClearChecklistSelection();
        ClearProjectSelection();
        RemindersButton.Classes.Add("selected");
        HomePanel.IsVisible = false;
        SettingsPanel.IsVisible = false;
        ShortcutsPanel.IsVisible = false;
        WorkspaceContent.IsVisible = false;
        WorkspaceContent.Content = null;
        HideHistory();
        RemindersPanel.IsVisible = true;
        TabBar.IsVisible = true;
        SetChecklistActionsEnabled(false);
        UpdateManualSaveVisibility();
        SyncProjectIndicators();
        SyncHomeTab();
        RefreshReminderRows();
    }

    private void RefreshReminderRows()
    {
        ReminderRows.Children.Clear();
        var reminders = ReminderSchedule.All();
        RemindersEmpty.IsVisible = reminders.Count == 0;
        foreach (var reminder in reminders)
            ReminderRows.Children.Add(ReminderRow(reminder));
    }

    private Control ReminderRow(ScheduledReminder reminder)
    {
        var when = ReminderText.Clock(reminder.NextOn);
        var repeat = reminder.EveryMinutes > 0 ? $" · {ReminderText.Interval(reminder.EveryMinutes)}" : string.Empty;
        var silent = reminder.Silent ? " · Silent" : string.Empty;
        var text = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(reminder.Title) ? "Checklist" : reminder.Title,
                    FontSize = AppearanceService.Font(15),
                    FontWeight = FontWeight.SemiBold,
                    Foreground = AppearanceService.Brush("TextPrimaryBrush"),
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = $"{ReminderKind(reminder)} · {when}{repeat}{silent}",
                    FontSize = AppearanceService.Font(12),
                    Foreground = AppearanceService.Brush("TextMutedBrush"),
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };

        var open = ReminderAction("Open");
        open.Click += (_, _) => OpenFromNotification(reminder.DocumentId, reminder.FilePath);
        var edit = ReminderAction("Edit");
        edit.Click += (_, _) => _ = EditReminderAsync(reminder);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 8
        };
        Grid.SetColumn(open, 1);
        Grid.SetColumn(edit, 2);
        row.Children.Add(text);
        row.Children.Add(open);
        row.Children.Add(edit);

        return new Border
        {
            Background = AppearanceService.Brush("SurfaceBrush"),
            BorderBrush = AppearanceService.Brush("BorderSubtleBrush"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(10),
            Padding = new Avalonia.Thickness(12),
            Child = row
        };
    }

    private async Task EditReminderAsync(ScheduledReminder reminder)
    {
        var current = ReminderSchedule.Find(reminder.DocumentId, reminder.Slot) ?? reminder;
        var dialog = new EditReminderWindow(current, string.IsNullOrEmpty(current.Slot));
        var edit = await dialog.ShowDialog<ReminderEditResult?>(this);
        if (edit is null)
            return;

        var tab = _tabs.FirstOrDefault(item => item.Document.Id == current.DocumentId);
        if (tab is not null)
        {
            ApplyReminderEdit(tab, current, edit);
            return;
        }

        if (_projects.Find(current.DocumentId) is { } project)
        {
            ApplyProjectReminderEdit(project, current, edit);
            return;
        }

        if (string.IsNullOrWhiteSpace(current.FilePath))
        {
            ShowToast("Open that checklist to change this reminder.");
            return;
        }

        await RunFileOperation(() => SaveClosedReminderAsync(current, edit));
    }

    private static void ApplyReminderEdit(ChecklistTab tab, ScheduledReminder reminder, ReminderEditResult edit)
    {
        if (string.IsNullOrEmpty(reminder.Slot))
        {
            tab.View.SetReminder(edit.Removed ? null : edit.NextUtc, edit.Removed ? 0 : edit.EveryMinutes, edit.Silent);
            return;
        }

        if (reminder.Slot == ScheduledReminder.DeadlineSlot)
        {
            tab.View.SetDeadlineNotify(edit.Removed ? null : tab.View.NotifyBeforeMinutes, edit.Silent);
            return;
        }

        if (!ScheduledReminder.TryItemId(reminder.Slot, out var itemId))
            return;

        var item = tab.View.Items.FirstOrDefault(entry => entry.Id == itemId);
        tab.View.SetItemDeadlineNotify(itemId, edit.Removed ? null : item?.NotifyBeforeMinutes, edit.Silent);
    }

    private async Task SaveClosedReminderAsync(ScheduledReminder reminder, ReminderEditResult edit)
    {
        var path = reminder.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        ChecklistDocument document;
        try
        {
            document = await _fileService.LoadAsync(path);
        }
        catch (ChecklistFileException ex)
        {
            await ShowMessage(ex.Message);
            return;
        }

        if (document.Id != reminder.DocumentId)
        {
            await ShowMessage("That checklist file has changed.");
            return;
        }

        if (string.IsNullOrEmpty(reminder.Slot))
        {
            document.ReminderOn = edit.Removed ? null : edit.NextUtc;
            document.ReminderEveryMinutes = edit.Removed ? 0 : edit.EveryMinutes;
            document.ReminderSilent = !edit.Removed && edit.Silent;
        }
        else if (reminder.Slot == ScheduledReminder.DeadlineSlot)
        {
            if (edit.Removed)
                document.NotifyBeforeMinutes = null;
            document.DeadlineSilent = !edit.Removed && edit.Silent && document.NotifyBeforeMinutes is not null;
        }
        else if (ScheduledReminder.TryItemId(reminder.Slot, out var itemId))
        {
            var item = document.Items.FirstOrDefault(entry => entry.Id == itemId);
            if (item is null)
            {
                await ShowMessage("That check is no longer in the checklist.");
                return;
            }

            if (edit.Removed)
                item.NotifyBeforeMinutes = null;
            item.DeadlineSilent = !edit.Removed && edit.Silent && item.NotifyBeforeMinutes is not null;
        }

        document.UpdatedOn = DateTime.UtcNow;
        await _fileService.SaveAsync(path, document, false);
        WriteClosedReminder(document, path, reminder.Slot);
        RefreshReminderRows();
    }

    private static void WriteClosedReminder(ChecklistDocument document, string path, string slot)
    {
        if (document.IsCompleted)
        {
            ReminderSchedule.Remove(document.Id);
            return;
        }

        if (string.IsNullOrEmpty(slot))
        {
            if (document.ReminderOn is not { } next)
            {
                ReminderSchedule.RemoveSlot(document.Id, string.Empty);
                return;
            }

            ReminderSchedule.Upsert(new ScheduledReminder
            {
                DocumentId = document.Id,
                Title = string.IsNullOrWhiteSpace(document.Title) ? "Checklist" : document.Title.Trim(),
                FilePath = path,
                NextOn = next,
                EveryMinutes = document.ReminderEveryMinutes,
                Silent = document.ReminderSilent
            });
            return;
        }

        if (slot == ScheduledReminder.DeadlineSlot)
        {
            WriteDeadlineSlot(document, path, slot, document.Deadline, document.DeadlineMinute, document.NotifyBeforeMinutes, document.DeadlineSilent, document.Title);
            return;
        }

        if (!ScheduledReminder.TryItemId(slot, out var itemId))
            return;

        var item = document.Items.FirstOrDefault(entry => entry.Id == itemId);
        if (item is null)
        {
            ReminderSchedule.RemoveSlot(document.Id, slot);
            return;
        }

        WriteDeadlineSlot(document, path, slot, item.Deadline, item.DeadlineMinute, item.NotifyBeforeMinutes, item.DeadlineSilent, item.Title);
    }

    private static void WriteDeadlineSlot(
        ChecklistDocument document,
        string path,
        string slot,
        DateOnly? deadline,
        int? minuteOfDay,
        int? minutesBefore,
        bool silent,
        string? title)
    {
        if (deadline is not { } date || minutesBefore is not int lead)
        {
            ReminderSchedule.RemoveSlot(document.Id, slot);
            return;
        }

        var when = DeadlineValue.Instant(date, minuteOfDay).AddMinutes(-Math.Max(0, lead));
        ReminderSchedule.Upsert(new ScheduledReminder
        {
            DocumentId = document.Id,
            Slot = slot,
            Title = string.IsNullOrWhiteSpace(title) ? "Checklist" : title.Trim(),
            Detail = DeadlineValue.NotifyText(lead),
            FilePath = path,
            NextOn = when,
            EveryMinutes = 0,
            Silent = silent
        });
    }

    private string ReminderKind(ScheduledReminder reminder)
    {
        var project = _projects.Find(reminder.DocumentId) is not null;
        if (string.IsNullOrEmpty(reminder.Slot))
            return project ? "Project reminder" : "Reminder";
        if (reminder.Slot == ScheduledReminder.DeadlineSlot)
            return project ? "Project deadline" : "Deadline";
        return "Check";
    }

    private static Button ReminderAction(string content) => new()
    {
        Content = content,
        Height = 34,
        Padding = new Avalonia.Thickness(12, 0),
        Background = AppearanceService.Brush("CardBrush"),
        Foreground = AppearanceService.Brush("TextSecondaryBrush"),
        BorderBrush = AppearanceService.Brush("BorderBrush"),
        BorderThickness = new Avalonia.Thickness(1),
        CornerRadius = new Avalonia.CornerRadius(8),
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    private void Theme_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<AppTheme>(tag, out var theme))
            AppearanceService.SetTheme(theme);
    }

    private void Density_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<AppDensity>(tag, out var density))
            AppearanceService.SetDensity(density);
    }

    private void FontSize_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<AppFontSize>(tag, out var fontSize))
            AppearanceService.SetFontSize(fontSize);
    }

    private void BuildAccentSwatches()
    {
        foreach (var accent in Enum.GetValues<AppAccent>())
        {
            var button = new Button
            {
                Classes = { "accent-swatch" },
                Tag = accent,
                Background = new SolidColorBrush(AppearanceService.Swatch(accent))
            };
            ToolTip.SetTip(button, accent.ToString());
            button.Click += (_, _) => AppearanceService.SetAccent(accent);
            AccentSwatches.Children.Add(button);
        }
    }

    private void SyncAppearanceSelection()
    {
        var theme = AppearanceService.Settings.Theme.ToString();
        var density = AppearanceService.Settings.Density.ToString();
        var fontSize = AppearanceService.Settings.FontSize.ToString();
        foreach (var button in this.GetLogicalDescendants().OfType<Button>())
        {
            if (!button.Classes.Contains("appearance-choice") || button.Tag is not string tag)
                continue;

            if (tag == theme || tag == density || tag == fontSize)
                button.Classes.Add("on");
            else
                button.Classes.Remove("on");
        }

        foreach (var child in AccentSwatches.Children.OfType<Button>())
        {
            if (child.Tag is AppAccent accent && accent == AppearanceService.Settings.Accent)
                child.Classes.Add("on");
            else
                child.Classes.Remove("on");
        }
    }

    private void BuildShortcutSettings()
    {
        ShortcutSections.Children.Clear();
        var onMac = OperatingSystem.IsMacOS();
        var muted = AppearanceService.Brush("TextSubtleBrush");
        var line = AppearanceService.Brush("BorderSubtleBrush");

        foreach (ShortcutGroup group in Enum.GetValues<ShortcutGroup>())
        {
            var shortcuts = AppShortcuts.All.Where(item => item.Group == group).ToList();
            if (shortcuts.Count == 0)
                continue;

            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(new TextBlock
            {
                Text = AppShortcuts.GroupTitle(group),
                FontSize = AppearanceService.Font(14),
                FontWeight = FontWeight.SemiBold,
                Foreground = AppearanceService.Brush("TextBodyBrush")
            });

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,160,168")
            };
            var row = 0;
            AddShortcutRow(grid, ref row, HeaderCell("Action", muted, left: true), HeaderCell("Windows", muted, left: false), HeaderCell("macOS", muted, left: false));
            AddShortcutLine(grid, ref row, line);

            foreach (var shortcut in shortcuts)
            {
                AddShortcutRow(
                    grid,
                    ref row,
                    new TextBlock
                    {
                        Text = shortcut.Action,
                        FontSize = AppearanceService.Font(14),
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Margin = new Avalonia.Thickness(12, 10)
                    },
                    ShortcutChip(shortcut.WindowsDisplay, current: !onMac),
                    ShortcutChip(shortcut.MacDisplay, current: onMac));
                if (shortcut != shortcuts[^1])
                    AddShortcutLine(grid, ref row, line);
            }

            section.Children.Add(new Border
            {
                Background = AppearanceService.Brush("SurfaceBrush"),
                BorderBrush = line,
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(12),
                Padding = new Avalonia.Thickness(4, 2),
                Child = grid
            });
            ShortcutSections.Children.Add(section);
        }
    }

    private static void AddShortcutRow(Grid grid, ref int row, Control action, Control windows, Control mac)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(action, row);
        Grid.SetColumn(action, 0);
        Grid.SetRow(windows, row);
        Grid.SetColumn(windows, 1);
        Grid.SetRow(mac, row);
        Grid.SetColumn(mac, 2);
        grid.Children.Add(action);
        grid.Children.Add(windows);
        grid.Children.Add(mac);
        row++;
    }

    private static void AddShortcutLine(Grid grid, ref int row, IBrush brush)
    {
        grid.RowDefinitions.Add(new RowDefinition(new GridLength(1)));
        var line = new Border
        {
            Height = 1,
            Background = brush,
            Margin = new Avalonia.Thickness(12, 0)
        };
        Grid.SetRow(line, row);
        Grid.SetColumnSpan(line, 3);
        grid.Children.Add(line);
        row++;
    }

    private static TextBlock HeaderCell(string text, IBrush foreground, bool left) =>
        new()
        {
            Text = text,
            FontSize = AppearanceService.Font(12),
            Foreground = foreground,
            HorizontalAlignment = left
                ? Avalonia.Layout.HorizontalAlignment.Left
                : Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(left ? 12 : 8, 10, 8, 4)
        };

    private static Border ShortcutChip(string text, bool current) =>
        new()
        {
            Background = AppearanceService.Brush(current ? AppearanceService.AccentSubtleBrush : AppearanceService.CardBrush),
            BorderBrush = AppearanceService.Brush(current ? AppearanceService.AccentHoverBrush : AppearanceService.BorderBrush),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(8, 4),
            Margin = new Avalonia.Thickness(8, 8),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Child = new TextBlock
            {
                Text = text,
                FontSize = AppearanceService.Font(12),
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Foreground = AppearanceService.Brush(current ? AppearanceService.AccentTextHoverBrush : AppearanceService.TextSecondaryBrush)
            }
        };

    private void ShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        var firstPress = _keysDown.Add(e.Key);
        if (e.Handled || !CanUseShortcuts())
            return;

        var shortcut = AppShortcuts.Match(e, ShortcutScope.Window);
        // Escape is handled after focused editors, so it can revert a field before closing a popup.
        if (shortcut is null || shortcut.Id == AppShortcuts.ClosePopup)
            return;

        e.Handled = true;
        if (!firstPress)
            return;

        InvokeShortcut(shortcut.Id);
    }

    private void ShortcutKeyUp(object? sender, KeyEventArgs e) =>
        _keysDown.Remove(e.Key);

    internal bool DismissTransientView()
    {
        if (_settingsOpen || _shortcutsOpen)
        {
            CloseSettings_Click(this, new RoutedEventArgs());
            return true;
        }

        if (_remindersOpen)
        {
            CloseReminders();
            return true;
        }

        return WorkspaceContent.IsVisible && ActiveTab()?.View.TryCloseDetail() == true;
    }

    private bool CanUseShortcuts()
    {
        if (!IsActive)
            return false;

        foreach (var window in OwnedWindows)
        {
            if (window.IsVisible)
                return false;
        }

        return true;
    }

    private bool TryBeginShortcut(string id)
    {
        var now = Environment.TickCount64;
        if (_lastShortcutId == id && now - _lastShortcutAt < 80)
            return false;

        _lastShortcutId = id;
        _lastShortcutAt = now;
        return true;
    }

    private void InvokeShortcut(string id)
    {
        if (!TryBeginShortcut(id))
            return;

        switch (id)
        {
            case AppShortcuts.NewChecklist:
                if (_settingsOpen || _shortcutsOpen)
                    break;
                ShowCreateChecklist();
                break;
            case AppShortcuts.CreateProject:
                if (_settingsOpen || _shortcutsOpen)
                    break;
                NewTabButton.Flyout?.Hide();
                ShowProjectEditor(null);
                break;
            case AppShortcuts.OpenChecklist:
                _ = RunFileOperation(OpenAsync);
                break;
            case AppShortcuts.Save:
                SaveActiveTab(saveAs: false);
                break;
            case AppShortcuts.SaveAs:
                SaveActiveTab(saveAs: true);
                break;
            case AppShortcuts.SaveAsTemplate:
                if (ActiveTab() is { } templating && !templating.Document.IsCompleted)
                {
                    if (_settingsOpen || _shortcutsOpen || _remindersOpen)
                        ActivateTab(templating);
                    templating.View.SaveAsTemplate();
                }
                break;
            case AppShortcuts.Rearrange:
                if (ActiveTab() is { } rearranging && !rearranging.Document.IsCompleted && rearranging.View.Items.Count > 1)
                {
                    if (_settingsOpen || _shortcutsOpen || _remindersOpen)
                        ActivateTab(rearranging);
                    rearranging.View.RearrangeChecks();
                }
                break;
            case AppShortcuts.Settings:
                if (_settingsOpen)
                    KeepSettingsOnTop();
                else
                    ShowSettings();
                break;
            case AppShortcuts.CloseTab:
                if (_settingsOpen || _shortcutsOpen)
                    break;
                if (ActiveTab() is { } closing)
                    _ = RunFileOperation(() => CloseTabAsync(closing));
                else if (_activeProjectTab is not null
                    && !HomePanel.IsVisible
                    && !_remindersOpen
                    && !_settingsOpen
                    && !_shortcutsOpen)
                    _ = RunFileOperation(() => CloseProjectTabAsync(_activeProjectTab));
                break;
            case AppShortcuts.NextTab:
                SelectTabRelative(1);
                break;
            case AppShortcuts.PreviousTab:
                SelectTabRelative(-1);
                break;
            case AppShortcuts.Search:
                if (ActiveTab() is { } searching)
                {
                    if (_settingsOpen || _shortcutsOpen || _remindersOpen)
                        ActivateTab(searching);
                    searching.View.FocusSearch();
                }
                break;
            case AppShortcuts.AddItem:
                if (ActiveTab() is { } adding && !adding.Document.IsCompleted)
                {
                    if (_settingsOpen || _shortcutsOpen || _remindersOpen)
                        ActivateTab(adding);
                    adding.View.AddCheck();
                }
                break;
        }
    }

    private void SelectTabRelative(int delta)
    {
        if (StripNeedsGroups())
        {
            SelectVisualTab(delta);
            return;
        }

        if (_tabs.Count == 0)
            return;

        var current = _activeTab is null ? -1 : _tabs.IndexOf(_activeTab);
        var next = current < 0
            ? (delta > 0 ? 0 : _tabs.Count - 1)
            : (current + delta + _tabs.Count) % _tabs.Count;
        ActivateTab(_tabs[next]);
    }

    private void Open_Click(object? sender, RoutedEventArgs e) =>
        _ = RunFileOperation(OpenAsync);

    private void NewChecklistMenu_Click(object? sender, EventArgs e) =>
        InvokeShortcut(AppShortcuts.NewChecklist);

    private void CreateProjectMenu_Click(object? sender, EventArgs e) =>
        InvokeShortcut(AppShortcuts.CreateProject);

    private void OpenMenu_Click(object? sender, EventArgs e) =>
        InvokeShortcut(AppShortcuts.OpenChecklist);

    private void Save_Click(object? sender, RoutedEventArgs e) =>
        SaveActiveTab(saveAs: false);

    private void SaveMenu_Click(object? sender, EventArgs e) =>
        InvokeShortcut(AppShortcuts.Save);

    private void SaveAs_Click(object? sender, RoutedEventArgs e) =>
        SaveActiveTab(saveAs: true);

    private void SaveAsMenu_Click(object? sender, EventArgs e) =>
        InvokeShortcut(AppShortcuts.SaveAs);

    private void ExportJsonMenu_Click(object? sender, EventArgs e)
    {
        if (ActiveTab() is { } tab)
            _ = RunFileOperation(() => SaveAsAsync(tab));
    }

    private void ExportCsvMenu_Click(object? sender, EventArgs e)
    {
        if (ActiveTab() is { } tab)
            _ = RunFileOperation(() => ExportCsvAsync(tab));
    }

    private void CopyTextMenu_Click(object? sender, EventArgs e)
    {
        if (ActiveTab() is { } tab)
            _ = CopyChecklistTextAsync(tab);
    }

    private void SettingsMenu_Click(object? sender, EventArgs e) =>
        InvokeShortcut(AppShortcuts.Settings);

    private void KeyboardShortcutsMenu_Click(object? sender, EventArgs e) =>
        ShowShortcuts();

    private void CheckForUpdatesMenu_Click(object? sender, EventArgs e) =>
        _ = RunUpdateCheckAsync(interactive: true, automatic: false);

    private void CopyMenu_Click(object? sender, EventArgs e)
    {
        if (ActiveTab() is not { } tab || tab.View.Items.Count == 0)
            return;

        _ = RunFileOperation(() => CopyChecklistAsync(tab));
    }

    private void AddCheckMenu_Click(object? sender, EventArgs e) =>
        InvokeShortcut(AppShortcuts.AddItem);

    private void SaveAsTemplateMenu_Click(object? sender, EventArgs e) =>
        InvokeShortcut(AppShortcuts.SaveAsTemplate);

    private void RearrangeMenu_Click(object? sender, EventArgs e) =>
        InvokeShortcut(AppShortcuts.Rearrange);

    private void PinTabMenu_Click(object? sender, EventArgs e)
    {
        if (ActiveTab() is { } tab && _tabs.Count > 1)
            TogglePin(tab);
    }

    private void DeleteChecksMenu_Click(object? sender, EventArgs e) =>
        ActiveTab()?.View.DeleteChecks();

    private void SaveActiveTab(bool saveAs)
    {
        if (ActiveTab() is not { } tab)
            return;

        if (!saveAs && HasSavedFile(tab))
        {
            ShowToast("We'll auto-save your changes.");
            return;
        }

        _ = RunFileOperation(() => saveAs ? SaveAsAsync(tab) : SaveAsync(tab));
    }

    private ChecklistTab? ActiveTab()
    {
        if (HomePanel.IsVisible || _historyOpen || ((_settingsOpen || _shortcutsOpen) && !_settingsOverChecklist))
            return null;

        return _activeTab;
    }

    private async Task RunFileOperation(Func<Task> action)
    {
        if (_fileOperationInProgress)
            return;

        _fileOperationInProgress = true;
        try
        {
            await action();
        }
        catch (ChecklistFileException ex)
        {
            await ShowMessage(ex.Message);
        }
        finally
        {
            _fileOperationInProgress = false;
            if (_pendingFileOpens.Count > 0)
            {
                var next = _pendingFileOpens.Dequeue();
                _ = RunFileOperation(() => OpenChecklistPathAsync(next));
            }
        }
    }

    private async Task OpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open checklist",
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

        await OpenChecklistPathAsync(path);
    }

    private void Recent_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
            return;

        _ = RunFileOperation(() => OpenChecklistPathAsync(path));
    }

    private async Task OpenChecklistPathAsync(string path)
    {
        var fullPath = NormalizePath(path);
        if (fullPath is null)
        {
            _recentChecklists.Remove(path);
            RefreshRecentList();
            await ShowMessage("That recent checklist could not be opened.");
            return;
        }

        // An open tab already has the latest edits, including ones not saved yet.
        // Switching to it must not read the file again.
        var alreadyOpen = FindTabByPath(fullPath);
        if (alreadyOpen is not null)
        {
            if (!alreadyOpen.IsDirty)
                RememberChecklist(fullPath, alreadyOpen.Document);
            else if (_recentChecklists.Find(fullPath) is { } saved)
                RememberChecklist(fullPath, saved);

            ActivateTab(alreadyOpen);
            return;
        }

        if (!File.Exists(fullPath))
        {
            var known = _recentChecklists.Find(fullPath);
            if (known is not null)
            {
                _recentChecklists.Remove(fullPath);
                RefreshRecentList();
                await ShowMessage(
                    $"Couldn't find \"{known.Title}\". It was removed from Recent checklists.");
                return;
            }

            await ShowMessage("That checklist file could not be found.");
            return;
        }

        var document = await _fileService.LoadAsync(fullPath);
        RememberChecklist(fullPath, document);
        OpenDocumentTab(document, fullPath, isDirty: false);
    }

    private async Task<bool> SaveAsync(ChecklistTab tab)
    {
        if (string.IsNullOrWhiteSpace(tab.FilePath))
            return await SaveAsAsync(tab);

        return await WriteDocumentAsync(tab, tab.FilePath);
    }

    private async Task<bool> SaveAsAsync(ChecklistTab tab)
    {
        var path = await PickSavePathAsync(tab);
        if (path is null)
            return false;

        return await WriteDocumentAsync(tab, path);
    }

    private async Task ExportCsvAsync(ChecklistTab tab)
    {
        CopyViewOntoDocument(tab);
        var path = await PickCsvPathAsync(tab.Document.Title);
        if (path is null)
            return;

        try
        {
            await File.WriteAllTextAsync(
                path,
                ChecklistShare.ToCsv(tab.Document, ChecklistFileName(tab)),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage("The checklist could not be exported.");
            return;
        }

        ShowToast($"Exported \"{tab.Document.Title}\".");
    }

    private async Task CopyChecklistTextAsync(ChecklistTab tab)
    {
        CopyViewOntoDocument(tab);
        if (Clipboard is null)
        {
            await ShowMessage("The clipboard isn't available.");
            return;
        }

        await Clipboard.SetTextAsync(ChecklistShare.ToPlainText(tab.Document));
        ShowToast($"Copied \"{tab.Document.Title}\" as text.");
    }

    private async Task<string?> PickCsvPathAsync(string title)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export checklist",
            SuggestedFileName = SuggestedCsvName(title),
            DefaultExtension = "csv",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("CSV")
                {
                    Patterns = ["*.csv"],
                    MimeTypes = ["text/csv"]
                }
            ]
        });

        if (file is null)
            return null;

        var path = LocalPath(file);
        if (path is null)
        {
            await ShowMessage("That location can't be used. Choose a file on this computer.");
            return null;
        }

        return EnsureCsvExtension(Path.GetFullPath(path));
    }

    private Task<string?> PickSavePathAsync(ChecklistTab tab) =>
        PickSavePathAsync(SuggestedFileName(tab.Document.Title));

    private async Task<string?> PickSavePathAsync(string suggestedFileName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save checklist",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "superdev.json",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("Super Dev checklist")
                {
                    Patterns = ["*.superdev.json"],
                    MimeTypes = ["application/json"]
                }
            ]
        });

        if (file is null)
            return null;

        var path = LocalPath(file);
        if (path is null)
        {
            await ShowMessage("That location can't be used. Choose a file on this computer.");
            return null;
        }

        return EnsureChecklistExtension(Path.GetFullPath(path));
    }

    private async Task<bool> WriteDocumentAsync(ChecklistTab tab, string path, bool announce = true)
    {
        var enablingAutoSave = announce && !HasSavedFile(tab);
        var other = FindTabByPath(path);
        if (other is not null && other != tab)
        {
            await ShowMessage(
                $"\"{other.Document.Title}\" is already open from that file. Choose a different file.");
            return false;
        }

        var replaceDifferentDocument = await PrepareOverwriteAsync(path, tab.Document.Id);
        if (replaceDifferentDocument is null)
            return false;

        var replacedId = await ReplacedDocumentIdAsync(path, tab.Document.Id, replaceDifferentDocument.Value);
        var version = tab.EditVersion;
        CopyViewOntoDocument(tab);

        // UpdatedOn advances only after the write succeeds. CreatedOn stays put.
        var previousUpdatedOn = tab.Document.UpdatedOn;
        tab.Document.UpdatedOn = DateTime.UtcNow;

        try
        {
            await _fileService.SaveAsync(path, tab.Document, replaceDifferentDocument.Value);
        }
        catch (Exception ex) when (ex is ChecklistFileException or IOException or UnauthorizedAccessException)
        {
            tab.Document.UpdatedOn = previousUpdatedOn;
            await ShowMessage(ex is ChecklistFileException fileEx
                ? fileEx.Message
                : "The checklist could not be saved.");
            return false;
        }

        tab.FilePath = path;
        tab.IsDirty = tab.EditVersion != version;
        PublishReminder(tab);
        tab.View.SetManualSaveVisible(!HasSavedFile(tab));
        UpdateTabChrome(tab);
        UpdateManualSaveVisibility();
        if (replacedId is Guid oldDocumentId)
            TrackDeleted(oldDocumentId, null);
        RememberChecklist(path, tab.Document, trackActivity: true);
        if (announce)
        {
            var saved = $"Saved \"{tab.Document.Title}\".";
            await ShowMessage(enablingAutoSave
                ? $"{saved} From now on, changes will be auto-saved."
                : saved);
        }
        return true;
    }

    private async Task CopyChecklistAsync(ChecklistTab source)
    {
        source.View.CommitDetails();

        var checks = source.View.Items.ToList();
        var destinations = _tabs.Where(tab => tab != source).ToList();
        var dialog = new CopyChecklistWindow(
            checks,
            destinations.Select(CopyTargetLabel).ToList());
        var choice = await dialog.ShowDialog<CopyChecklistChoice?>(this);
        if (choice is null)
            return;

        var selected = choice.ItemIndexes
            .Where(index => index >= 0 && index < checks.Count)
            .Distinct()
            .Select(index => checks[index])
            .ToList();
        if (selected.Count == 0)
        {
            await ShowMessage("Choose at least one check to copy.");
            return;
        }

        if (choice.DestinationIndex < 0)
        {
            await SaveCopyAsNewFileAsync(source, selected);
            return;
        }

        if (choice.DestinationIndex >= destinations.Count)
            return;

        var destination = destinations[choice.DestinationIndex];
        var copySource = DescribeCopySource(source);
        var added = destination.View.AppendCopies(selected, copySource.Name, copySource.Path);
        ActivateTab(destination);
        await ShowMessage(
            $"Copied {added} {(added == 1 ? "check" : "checks")} to \"{destination.Document.Title}\".");
    }

    private async Task SaveCopyAsNewFileAsync(
        ChecklistTab source,
        IReadOnlyList<ChecklistItem> items)
    {
        var path = await PickSavePathAsync(source);
        if (path is null)
            return;

        if (string.Equals(NormalizePath(source.FilePath), NormalizePath(path), StringComparison.OrdinalIgnoreCase))
        {
            await ShowMessage("Choose a different file. This checklist is already saved there.");
            return;
        }

        var other = FindTabByPath(path);
        if (other is not null)
        {
            await ShowMessage(
                $"\"{other.Document.Title}\" is already open from that file. Choose a different file.");
            return;
        }

        var now = DateTime.UtcNow;
        var copySource = DescribeCopySource(source);
        var copy = new ChecklistDocument
        {
            FormatVersion = ChecklistDocument.CurrentFormatVersion,
            Id = Guid.NewGuid(),
            Title = source.View.WorkTitle,
            Description = source.View.Description,
            BranchName = source.View.BranchName,
            PullRequestUrl = source.View.PullRequestUrl,
            Deadline = source.View.Deadline,
            DeadlineMinute = source.View.DeadlineMinute,
            NotifyBeforeMinutes = source.View.NotifyBeforeMinutes,
            DeadlineSilent = source.View.DeadlineSilent,
            ReminderOn = source.View.ReminderOn,
            ReminderEveryMinutes = source.View.ReminderEveryMinutes,
            ReminderSilent = source.View.ReminderSilent,
            WorkType = source.View.WorkType,
            TemplateName = source.View.TemplateName,
            TaskDescription = source.View.TaskDescription,
            AiPlatform = source.View.AiPlatform,
            AiFramework = source.View.AiFramework,
            CreatedOn = now,
            UpdatedOn = now,
            IsCompleted = items.Count == source.View.Items.Count &&
                          source.Document.IsCompleted &&
                          !source.View.HasIncompleteRequiredChecks(),
            Items = items.Select(item => CopyItem(item, copySource.Name, copySource.Path)).ToList()
        };

        var replaceDifferentDocument = await PrepareOverwriteAsync(path, copy.Id);
        if (replaceDifferentDocument is null)
            return;

        var replacedCopyId = await ReplacedDocumentIdAsync(path, copy.Id, replaceDifferentDocument.Value);
        try
        {
            await _fileService.SaveAsync(path, copy, replaceDifferentDocument.Value);
        }
        catch (Exception ex) when (ex is ChecklistFileException or IOException or UnauthorizedAccessException)
        {
            await ShowMessage(ex is ChecklistFileException fileEx
                ? fileEx.Message
                : "The checklist could not be saved.");
            return;
        }

        if (replacedCopyId is Guid oldCopyId)
            TrackDeleted(oldCopyId, null);
        TrackCreated(copy, path);
        RememberChecklist(path, copy, trackActivity: true);
        OpenDocumentTab(copy, path, isDirty: false);
        await ShowMessage($"Saved a copy of \"{copy.Title}\".");
    }

    private static string CopyTargetLabel(ChecklistTab tab)
    {
        if (string.IsNullOrWhiteSpace(tab.FilePath))
            return $"{tab.Document.Title} · Not saved yet";

        return $"{tab.Document.Title} · {Path.GetFileName(tab.FilePath)}";
    }

    private readonly record struct CopySource(string Name, string? Path);

    private static CopySource DescribeCopySource(ChecklistTab source)
    {
        if (!string.IsNullOrWhiteSpace(source.FilePath))
        {
            var path = NormalizePath(source.FilePath) ?? source.FilePath;
            return new CopySource(Path.GetFileName(path), path);
        }

        var title = source.View.WorkTitle?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = source.Document.Title?.Trim();

        return new CopySource(string.IsNullOrWhiteSpace(title) ? "Untitled" : title, null);
    }

    private static ChecklistItem CopyItem(ChecklistItem item, string copiedFrom, string? copiedFromPath) =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = item.Title,
            Category = item.Category,
            Priority = item.Priority,
            Notes = item.Notes,
            Acceptance = item.Acceptance,
            ItemType = item.ItemType,
            TestReports = item.TestReports,
            Logs = item.Logs,
            CodeReferences = item.CodeReferences,
            ApiResponses = item.ApiResponses,
            PullRequestLinks = item.PullRequestLinks,
            Evidence = (item.Evidence ?? [])
                .Select(file => new EvidenceFile { Path = file.Path })
                .ToList(),
            Deadline = item.Deadline,
            DeadlineMinute = item.DeadlineMinute,
            NotifyBeforeMinutes = item.NotifyBeforeMinutes,
            DeadlineSilent = item.DeadlineSilent,
            Subtasks = (item.Subtasks ?? [])
                .Select(subtask => new ChecklistSubtask
                {
                    Id = Guid.NewGuid(),
                    Title = subtask.Title,
                    IsCompleted = subtask.IsCompleted,
                    LinkedDocumentId = subtask.LinkedDocumentId,
                    LinkedFilePath = subtask.LinkedFilePath,
                    LinkOptional = subtask.LinkOptional
                })
                .ToList(),
            IsCompleted = item.IsSkipped ? false : item.IsCompleted,
            IsSkipped = item.IsSkipped,
            CopiedFrom = copiedFrom,
            CopiedFromPath = copiedFromPath
        };

    private async Task OpenCopiedFromAsync(string path)
    {
        var fullPath = NormalizePath(path);
        if (fullPath is not null)
        {
            var alreadyOpen = FindTabByPath(fullPath);
            if (alreadyOpen is not null)
            {
                ActivateTab(alreadyOpen);
                return;
            }
        }

        if (fullPath is null || !File.Exists(fullPath))
        {
            var name = string.IsNullOrWhiteSpace(fullPath)
                ? "That checklist"
                : Path.GetFileName(fullPath);
            await ShowMessage(
                $"Couldn't open \"{name}\". The file was removed or renamed.");
            return;
        }

        await OpenChecklistPathAsync(fullPath);
    }

    /// <summary>
    /// Null means the user cancelled. False means the file can be written as this
    /// document. True means the file belongs to a different document and the user
    /// confirmed replacing it.
    /// </summary>
    private async Task<bool?> PrepareOverwriteAsync(string path, Guid documentId)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            var existing = await _fileService.LoadAsync(path);
            if (existing.Id == documentId)
                return false;

            var overwrite = await ConfirmAsync(
                "Overwrite a different checklist?",
                $"That file is \"{existing.Title}\", a different checklist. Overwriting it replaces that document.",
                "Overwrite",
                "Cancel",
                danger: true);

            return overwrite ? true : null;
        }
        catch (ChecklistFileException)
        {
            var replace = await ConfirmAsync(
                "Replace this file?",
                "That file isn't a Super Dev checklist. Replace it with this checklist?",
                "Replace",
                "Cancel",
                danger: true);

            return replace ? true : null;
        }
    }

    private void RememberChecklist(string path, ChecklistDocument document, bool trackActivity = false)
    {
        _projects.Note(document, path);
        RememberChecklist(path, document.Title, document.WorkType, document.IsCompleted);
        if (trackActivity)
            TrackCaptured(document, path);
        RefreshProjectSurfaces();
    }

    private void RememberChecklist(
        string path,
        RecentChecklistEntry saved) =>
        RememberChecklist(path, saved.Title, saved.WorkType, saved.IsCompleted);

    private void RememberChecklist(
        string path,
        string title,
        WorkType workType,
        bool isCompleted)
    {
        _recentChecklists.Remember(path, title, workType, isCompleted);
        RefreshRecentList();
    }

    private readonly record struct HomeFile(
        string Path,
        string Title,
        WorkType? WorkType,
        DateTime WhenUtc,
        bool Completed,
        bool Missing);

    private void RefreshRecentList()
    {
        FillFileRows(RecentItems, RecentHomeFiles());
        var empty = RecentItems.Children.Count == 0;
        RecentEmpty.IsVisible = empty;
        RecentItems.IsVisible = !empty;
        if (AllFilesPanel.IsVisible)
            RefreshAllFiles();
    }

    private void RefreshAllFiles()
    {
        var files = AllKnownFiles();
        FillFileRows(AllFileItems, files);
        AllFilesEmpty.IsVisible = files.Count == 0;
        AllFileItems.IsVisible = files.Count > 0;
    }

    private List<HomeFile> RecentHomeFiles()
    {
        var files = new List<HomeFile>();
        foreach (var entry in _recentChecklists.Entries)
        {
            var open = FindTabByPath(entry.Path);
            files.Add(new HomeFile(
                entry.Path,
                string.IsNullOrWhiteSpace(open?.Document.Title) ? entry.Title : open.Document.Title,
                open?.Document.WorkType ?? entry.WorkType,
                entry.LastUsedOn,
                open?.Document.IsCompleted ?? entry.IsCompleted,
                open is null && !File.Exists(entry.Path)));
        }

        return files;
    }

    private List<HomeFile> AllKnownFiles()
    {
        var map = new Dictionary<string, HomeFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in RecentHomeFiles())
        {
            var key = NormalizePath(file.Path) ?? file.Path;
            map[key] = file;
        }

        foreach (var record in _projects.Tasklists)
        {
            if (string.IsNullOrWhiteSpace(record.Path))
                continue;

            var key = NormalizePath(record.Path) ?? record.Path;
            if (map.ContainsKey(key))
                continue;

            var open = FindTabByPath(record.Path);
            map[key] = new HomeFile(
                record.Path,
                string.IsNullOrWhiteSpace(open?.Document.Title) ? record.Title : open.Document.Title,
                open?.Document.WorkType,
                record.UpdatedOn,
                open?.Document.IsCompleted ?? record.IsCompleted,
                open is null && !File.Exists(record.Path));
        }

        return map.Values.OrderByDescending(file => file.WhenUtc).ToList();
    }

    private void FillFileRows(Panel host, IReadOnlyList<HomeFile> files)
    {
        host.Children.Clear();
        foreach (var file in files)
            host.Children.Add(FileRow(file));
    }

    private Control FileRow(HomeFile file)
    {
        var open = FindTabByPath(file.Path);
        var title = new TextBlock
        {
            Text = file.Title,
            FontSize = AppearanceService.Font(14),
            FontWeight = FontWeight.SemiBold,
            Foreground = AppearanceService.Brush(AppearanceService.TextPrimaryBrush),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var detail = new TextBlock
        {
            Text = FileSubtitle(file, open),
            FontSize = AppearanceService.Font(12),
            Foreground = AppearanceService.Brush(file.Missing ? AppearanceService.DangerTextBrush : AppearanceService.TextMutedBrush),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var text = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { title, detail }
        };
        var category = FileCategory(file.WorkType);
        var tint = FileTintFor(file.WorkType);
        var pill = new Border
        {
            Background = tint.Plate,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = category,
                FontSize = AppearanceService.Font(12),
                FontWeight = FontWeight.SemiBold,
                Foreground = tint.Glyph
            }
        };
        var when = new TextBlock
        {
            Text = FormatWhen(file.WhenUtc),
            FontSize = AppearanceService.Font(12),
            Foreground = AppearanceService.Brush(AppearanceService.TextMutedBrush),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right
        };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            ColumnSpacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        var mark = FileMark(tint.Plate, tint.Glyph);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(pill, 2);
        Grid.SetColumn(when, 3);
        row.Children.Add(mark);
        row.Children.Add(text);
        row.Children.Add(pill);
        row.Children.Add(when);

        var button = new Button
        {
            Classes = { "recent-file" },
            Tag = file.Path,
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        ToolTip.SetTip(button, file.Path);
        button.Click += Recent_Click;
        return button;
    }

    private string FileSubtitle(HomeFile file, ChecklistTab? open)
    {
        if (file.Missing)
            return "File missing";

        var project = ProjectNameFor(file.Path);
        var text = string.IsNullOrWhiteSpace(project) ? "Standalone checklist" : project;
        if (open?.IsDirty == true)
            text += " · Unsaved changes";
        return text;
    }

    private string? ProjectNameFor(string path)
    {
        var full = NormalizePath(path);
        if (full is null)
            return null;

        foreach (var record in _projects.Tasklists)
        {
            if (!string.Equals(NormalizePath(record.Path), full, StringComparison.OrdinalIgnoreCase))
                continue;
            if (record.ProjectId is not Guid id)
                return null;
            return _projects.Find(id)?.Name;
        }

        return null;
    }

    private static string FileCategory(WorkType? workType) =>
        workType is { } value ? WorkflowLabel(value) : "Tasklist";

    private readonly record struct FileColors(IBrush Plate, IBrush Glyph);

    private static FileColors FileTintFor(WorkType? workType)
    {
        IBrush Plate(string key) => AppearanceService.Brush(key);
        return workType switch
        {
            WorkType.Bug => new FileColors(Plate(AppearanceService.DangerSurfaceBrush), Plate(AppearanceService.DangerTextBrush)),
            WorkType.Feature => new FileColors(Plate(AppearanceService.AccentSubtleBrush), Plate(AppearanceService.AccentTextBrush)),
            WorkType.Development => new FileColors(Plate(AppearanceService.SuccessSubtleBrush), Plate(AppearanceService.SuccessTextBrush)),
            WorkType.Testing => new FileColors(Plate(AppearanceService.InfoSurfaceBrush), Plate(AppearanceService.InfoTextBrush)),
            WorkType.PullRequest => new FileColors(Plate(AppearanceService.ReviewSurfaceBrush), Plate(AppearanceService.ReviewTextBrush)),
            WorkType.Daily => new FileColors(Plate(AppearanceService.WarningSurfaceBrush), Plate(AppearanceService.WarningTextBrush)),
            _ => new FileColors(Plate(AppearanceService.CardBrush), Plate(AppearanceService.TextSecondaryBrush))
        };
    }

    private static Control FileMark(IBrush plate, IBrush glyph)
    {
        return new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(10),
            Background = plate,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Viewbox
            {
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                Child = new Avalonia.Controls.Shapes.Path
                {
                    Width = 16,
                    Height = 16,
                    Stretch = Stretch.Uniform,
                    Stroke = glyph,
                    StrokeThickness = 1.6,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    Fill = Brushes.Transparent,
                    Data = Geometry.Parse("M3,1.5 H9 L12.5,5 V14.5 H3 Z M9,1.5 V5 H12.5")
                }
            }
        };
    }

    private static string FormatWhen(DateTime value)
    {
        var local = value.Kind switch
        {
            DateTimeKind.Local => value,
            DateTimeKind.Utc => value.ToLocalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime()
        };
        var time = local.ToString("h:mm tt", CultureInfo.CurrentCulture);
        if (local.Date == DateTime.Today)
            return $"Today, {time}";
        if (local.Date == DateTime.Today.AddDays(-1))
            return $"Yesterday, {time}";
        return local.ToString("d MMM yyyy, h:mm tt", CultureInfo.CurrentCulture);
    }

    private static string WorkflowLabel(WorkType workType) =>
        workType switch
        {
            WorkType.Bug => "Bug Fix",
            WorkType.Feature => "Feature",
            WorkType.Development => "Development",
            WorkType.Testing => "Testing",
            WorkType.PullRequest => "Pull Request",
            WorkType.Blank => "Blank",
            WorkType.Daily => "Daily",
            WorkType.Custom => "Custom",
            _ => workType.ToString()
        };

    private static void CopyViewOntoDocument(ChecklistTab tab)
    {
        tab.View.CommitDetails();
        tab.Document.Title = tab.View.WorkTitle;
        tab.Document.Description = tab.View.Description;
        tab.Document.BranchName = tab.View.BranchName;
        tab.Document.PullRequestUrl = tab.View.PullRequestUrl;
        tab.Document.Deadline = tab.View.Deadline;
        tab.Document.DeadlineMinute = tab.View.DeadlineMinute;
        tab.Document.NotifyBeforeMinutes = tab.View.NotifyBeforeMinutes;
        tab.Document.DeadlineSilent = tab.View.DeadlineSilent;
        tab.Document.ReminderOn = tab.View.ReminderOn;
        tab.Document.ReminderEveryMinutes = tab.View.ReminderEveryMinutes;
        tab.Document.ReminderSilent = tab.View.ReminderSilent;
        tab.Document.WorkType = tab.View.WorkType;
        tab.Document.TemplateName = tab.View.TemplateName;
        tab.Document.TaskDescription = tab.View.TaskDescription;
        tab.Document.AiPlatform = tab.View.AiPlatform;
        tab.Document.AiFramework = tab.View.AiFramework;
        tab.Document.Items = tab.View.Items.ToList();
    }

    private void OnDocumentEdited(ChecklistTab tab)
    {
        tab.EditVersion++;
        tab.IsDirty = true;
        tab.Document.Title = tab.View.WorkTitle;
        tab.Document.Description = tab.View.Description;
        tab.Document.BranchName = tab.View.BranchName;
        tab.Document.PullRequestUrl = tab.View.PullRequestUrl;
        tab.Document.Deadline = tab.View.Deadline;
        tab.Document.DeadlineMinute = tab.View.DeadlineMinute;
        tab.Document.NotifyBeforeMinutes = tab.View.NotifyBeforeMinutes;
        tab.Document.DeadlineSilent = tab.View.DeadlineSilent;
        tab.Document.ReminderOn = tab.View.ReminderOn;
        tab.Document.ReminderEveryMinutes = tab.View.ReminderEveryMinutes;
        tab.Document.ReminderSilent = tab.View.ReminderSilent;
        tab.Document.TemplateName = tab.View.TemplateName;
        tab.TitleText.Text = tab.Document.Title;

        // Opening never reaches this. Completion stays until a Critical or Required
        // check is incomplete, so the flag still means those checks passed.
        if (tab.Document.IsCompleted && tab.View.HasIncompleteRequiredChecks())
            tab.Document.IsCompleted = false;

        tab.View.SetDocumentCompleted(tab.Document.IsCompleted);
        UpdateTabChrome(tab);
        if (ActiveTab() == tab)
            SetChecklistActionsEnabled(true);
        PublishLinkedCompletion(tab);
        ScheduleAutoSave(tab);
        SyncReminders();
        RefreshProjectSurfaces();
    }

    private void OnDocumentCompleted(ChecklistTab tab)
    {
        if (tab.Document.IsCompleted)
            return;

        tab.EditVersion++;
        tab.Document.IsCompleted = true;
        tab.IsDirty = true;
        tab.View.SetDocumentCompleted(true);
        SuperDev.SuccessSound.PlayComplete();
        UpdateTabChrome(tab);
        PublishLinkedCompletion(tab);
        ScheduleAutoSave(tab);
        SyncReminders();
        RefreshProjectSurfaces();
        if (ActiveTab() == tab)
            SetChecklistActionsEnabled(true);
    }

    private void OnDocumentReopened(ChecklistTab tab)
    {
        if (!tab.Document.IsCompleted)
            return;

        tab.EditVersion++;
        tab.Document.IsCompleted = false;
        tab.IsDirty = true;
        tab.View.SetDocumentCompleted(false);
        UpdateTabChrome(tab);
        PublishLinkedCompletion(tab);
        ScheduleAutoSave(tab);
        SyncReminders();
        RefreshProjectSurfaces();
        if (ActiveTab() == tab)
            SetChecklistActionsEnabled(true);
    }

    private void SyncLinkedChecklists(ChecklistTab source)
    {
        foreach (var other in _tabs)
        {
            if (other == source)
                continue;

            source.View.ApplyLinkedCompletion(
                other.Document.Id,
                other.FilePath,
                other.Document.IsCompleted,
                other.Document.Title);
            other.View.ApplyLinkedCompletion(
                source.Document.Id,
                source.FilePath,
                source.Document.IsCompleted,
                source.Document.Title);
        }
    }

    private void PublishLinkedCompletion(ChecklistTab source)
    {
        foreach (var other in _tabs)
        {
            if (other == source)
                continue;

            other.View.ApplyLinkedCompletion(
                source.Document.Id,
                source.FilePath,
                source.Document.IsCompleted,
                source.Document.Title);
        }
    }

    private async Task OpenLinkedSubtaskAsync(ChecklistSubtask subtask)
    {
        var open = FindLinkedTab(subtask);
        if (open is not null)
        {
            ActivateTab(open);
            return;
        }

        if (string.IsNullOrWhiteSpace(subtask.LinkedFilePath))
        {
            ShowToast("That checklist isn't open.");
            return;
        }

        await RunFileOperation(() => OpenChecklistPathAsync(subtask.LinkedFilePath));
    }

    private async Task<ChecklistSubtask?> PickLinkedChecklistAsync(ChecklistTab current, Window owner)
    {
        var others = _tabs.Where(tab => tab.Document.Id != current.Document.Id).ToList();
        var dialog = new LinkChecklistWindow(others
            .Select(tab => tab.Document.IsCompleted
                ? $"{tab.Document.Title} · Completed"
                : tab.Document.Title)
            .ToList());
        var choice = await dialog.ShowDialog<LinkChecklistChoice?>(owner);
        if (choice is null)
            return null;

        if (choice.ChooseFile)
            return await PickLinkedFileAsync(current, owner);

        if (choice.OpenIndex is not int index || index < 0 || index >= others.Count)
            return null;

        var selected = others[index];
        return CreateLinkedSubtask(selected.Document, selected.FilePath);
    }

    private async Task<ChecklistSubtask?> PickLinkedFileAsync(ChecklistTab current, Window owner)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Link checklist",
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
            return null;

        var path = LocalPath(files[0]);
        if (path is null)
        {
            await ShowMessage(owner, "That location can't be used. Choose a file on this computer.");
            return null;
        }

        path = NormalizePath(path) ?? path;
        var open = FindTabByPath(path);
        if (open is not null)
        {
            if (open.Document.Id == current.Document.Id)
            {
                await ShowMessage(owner, "Choose a different checklist.");
                return null;
            }

            return CreateLinkedSubtask(open.Document, open.FilePath ?? path);
        }

        ChecklistDocument document;
        try
        {
            document = await _fileService.LoadAsync(path);
        }
        catch (Exception ex) when (ex is ChecklistFileException or IOException or UnauthorizedAccessException)
        {
            await ShowMessage(owner, ex is ChecklistFileException fileEx
                ? fileEx.Message
                : "That checklist file could not be opened.");
            return null;
        }

        if (document.Id == current.Document.Id)
        {
            await ShowMessage(owner, "Choose a different checklist.");
            return null;
        }

        var alreadyOpen = _tabs.FirstOrDefault(tab => tab.Document.Id == document.Id);
        if (alreadyOpen is not null)
            return CreateLinkedSubtask(alreadyOpen.Document, alreadyOpen.FilePath ?? path);

        return CreateLinkedSubtask(document, path);
    }

    private ChecklistTab? FindLinkedTab(ChecklistSubtask subtask)
    {
        if (subtask.LinkedDocumentId is Guid id)
        {
            var byId = _tabs.FirstOrDefault(tab => tab.Document.Id == id);
            if (byId is not null)
                return byId;
        }

        if (string.IsNullOrWhiteSpace(subtask.LinkedFilePath))
            return null;

        return FindTabByPath(subtask.LinkedFilePath);
    }

    private static ChecklistSubtask CreateLinkedSubtask(ChecklistDocument document, string? filePath) =>
        new()
        {
            Title = document.Title,
            LinkedDocumentId = document.Id,
            LinkedFilePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath,
            IsCompleted = document.IsCompleted
        };

    private async Task ShowMessage(Window owner, string message)
    {
        var dialog = new StyledDialog("Super Dev", message);
        await dialog.ShowDialog(owner);
    }

    private void UpdateTabChrome(ChecklistTab tab)
    {
        tab.CompletionMark.IsVisible = tab.Document.IsCompleted;

        var location = tab.IsSaving
            ? "Saving…"
            : tab.IsDirty
                ? "Unsaved changes"
                : tab.FilePath ?? "Not saved yet";

        var tip = tab.Document.IsCompleted
            ? $"{tab.Document.Title} — Completed — {location}"
            : $"{tab.Document.Title} — {location}";
        var membership = ProjectNameFor(tab);
        if (membership is not null)
            tip = $"In {membership} — {tip}";

        ToolTip.SetTip(tab.HeaderContainer, tip);
        ToolTip.SetTip(tab.TitleText, tip);
        UpdateProjectCaption(tab);
    }

    /// <summary>
    /// A checklist opened on its own still names the project it belongs to, under the tab title.
    /// One opened from that project is already grouped beside the project tab.
    /// </summary>
    private void UpdateProjectCaption(ChecklistTab tab)
    {
        var name = tab.OpenedFromProjectId is null ? ProjectNameFor(tab) : null;
        var show = !string.IsNullOrWhiteSpace(name);
        tab.ProjectLabel.IsVisible = show;
        tab.HeaderContainer.Height = 40;
        tab.TitleText.TextAlignment = show ? TextAlignment.Left : TextAlignment.Center;
        tab.TitleText.HorizontalAlignment = show
            ? Avalonia.Layout.HorizontalAlignment.Left
            : Avalonia.Layout.HorizontalAlignment.Center;
        if (!show)
        {
            tab.HeaderContainer.Padding = new Avalonia.Thickness(8, 3);
            return;
        }

        tab.ProjectLabel.Text = name;
        tab.ProjectLabel.TextAlignment = TextAlignment.Left;
        tab.ProjectLabel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        if (tab.Document.ProjectId is Guid projectId && _projects.Find(projectId) is { } project)
            tab.ProjectLabel.Foreground = ProjectChrome.Brush(project.Color);
        ToolTip.SetTip(tab.ProjectLabel, name);
        tab.HeaderContainer.Padding = new Avalonia.Thickness(8, 1);
    }

    private string? ProjectNameFor(ChecklistTab tab)
    {
        if (tab.Document.ProjectId is not Guid projectId)
            return null;

        return _projects.Find(projectId)?.Name;
    }

    private static bool HasSavedFile(ChecklistTab tab) =>
        !string.IsNullOrWhiteSpace(tab.FilePath);

    private void UpdateManualSaveVisibility()
    {
        var manual = ActiveTab() is { } tab && !HasSavedFile(tab);
        if (_saveMenuItem is not null)
            _saveMenuItem.IsVisible = manual;

        WindowSaveMenuItem.IsVisible = manual;
    }

    private void ScheduleAutoSave(ChecklistTab tab)
    {
        if (!HasSavedFile(tab) || _tabs.IndexOf(tab) < 0)
            return;

        if (tab.AutoSaveTimer is null)
        {
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(AutoSaveDelayMilliseconds)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _ = AutoSaveAsync(tab);
            };
            tab.AutoSaveTimer = timer;
        }

        tab.AutoSaveTimer.Stop();
        tab.AutoSaveTimer.Start();
    }

    private async Task AutoSaveAsync(ChecklistTab tab)
    {
        if (_tabs.IndexOf(tab) < 0 || !HasSavedFile(tab) || !tab.IsDirty)
            return;

        if (_fileOperationInProgress || tab.AutoSaveTask is not null)
        {
            ScheduleAutoSave(tab);
            return;
        }

        var task = PerformAutoSaveAsync(tab);
        tab.AutoSaveTask = task;
        bool? saved;
        try
        {
            saved = await task;
        }
        finally
        {
            if (tab.AutoSaveTask == task)
                tab.AutoSaveTask = null;
        }

        if (_tabs.IndexOf(tab) < 0 || !tab.IsDirty || !HasSavedFile(tab))
            return;

        // A failed write already explained itself. Wait for the next edit before trying again.
        if (saved != false)
            ScheduleAutoSave(tab);
    }

    /// <summary>Null when another file operation was already running.</summary>
    private async Task<bool?> PerformAutoSaveAsync(ChecklistTab tab)
    {
        SetSaving(tab, true);
        var ran = false;
        var saved = false;
        var announced = false;
        try
        {
            await RunFileOperation(async () =>
            {
                ran = true;
                announced = ShowSaveToastIfDue();
                saved = await WriteDocumentAsync(tab, tab.FilePath!, announce: false);
            });
        }
        finally
        {
            if (_tabs.IndexOf(tab) >= 0)
                SetSaving(tab, false);
            if (ran && !saved && announced)
                HideToast();
        }

        return ran ? saved : null;
    }

    private void SetSaving(ChecklistTab tab, bool saving)
    {
        tab.IsSaving = saving;
        UpdateTabChrome(tab);
    }

    private bool ShowSaveToastIfDue()
    {
        var now = DateTime.UtcNow;
        if (now - _lastSaveToastUtc < SaveToastInterval)
            return false;

        _lastSaveToastUtc = now;
        ShowToast("Saving your changes…");
        return true;
    }

    private void StartReminderTimer()
    {
        EnsureDaemon();
        RefreshNotificationBell();
        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _reminderTimer.Tick += (_, _) => SyncReminders();
        _reminderTimer.Start();
    }

    private void EnsureDaemon()
    {
        if (ReminderDaemon.IsRunning())
            return;

        if (DateTime.UtcNow - _daemonEnsuredUtc < TimeSpan.FromSeconds(15))
            return;

        _daemonEnsuredUtc = DateTime.UtcNow;
        ReminderDaemon.EnsureRunning();
    }

    private void SyncReminders()
    {
        EnsureDaemon();

        foreach (var tab in _tabs.ToList())
        {
            if (tab.Document.IsCompleted)
            {
                ReminderSchedule.Remove(tab.Document.Id);
                ReminderSchedule.DismissDocument(tab.Document.Id);
                continue;
            }

            PublishDeadlineReminders(tab);

            if (tab.View.ReminderOn is null)
            {
                ReminderSchedule.RemoveSlot(tab.Document.Id, string.Empty);
                continue;
            }

            var current = tab.View.ReminderOn.Value;
            var every = tab.View.ReminderEveryMinutes;
            var scheduled = ReminderSchedule.Find(tab.Document.Id);
            if (scheduled is not null &&
                scheduled.EveryMinutes == every &&
                scheduled.NextOn > current)
            {
                if (scheduled.Silent != tab.View.ReminderSilent ||
                    scheduled.Title != tab.Document.Title ||
                    !string.Equals(scheduled.FilePath, tab.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    ReminderSchedule.Touch(
                        tab.Document.Id,
                        string.Empty,
                        ChecklistTitle(tab),
                        tab.FilePath,
                        tab.View.ReminderSilent);
                }

                if (tab.View.ApplyExternalReminder(scheduled.NextOn, scheduled.EveryMinutes))
                    PersistExternalReminder(tab);
                continue;
            }

            if (scheduled is null && ReminderSchedule.WasDelivered(tab.Document.Id, current))
            {
                if (every <= 0)
                {
                    if (tab.View.ApplyExternalReminder(null, 0))
                        PersistExternalReminder(tab);
                }
                else if (tab.View.ApplyExternalReminder(ReminderSchedule.Advance(current, every, DateTime.UtcNow), every))
                {
                    PublishReminder(tab);
                    PersistExternalReminder(tab);
                }

                continue;
            }

            if (scheduled is null ||
                scheduled.NextOn != current ||
                scheduled.EveryMinutes != every ||
                scheduled.Silent != tab.View.ReminderSilent ||
                scheduled.Title != tab.Document.Title ||
                !string.Equals(scheduled.FilePath, tab.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                PublishReminder(tab);
            }
        }

        SyncProjectSchedules();
        ReminderSchedule.DeliverDue();
        PullDeliveredReminders();
        RefreshNotificationBell();
        if (RemindersPanel.IsVisible)
            RefreshReminderRows();
        foreach (var tab in _tabs)
            tab.View.RefreshDeadlineAppearance();
    }

    private void PullDeliveredReminders()
    {
        foreach (var tab in _tabs.ToList())
        {
            if (tab.Document.IsCompleted || tab.View.ReminderOn is not { } current)
                continue;

            var every = tab.View.ReminderEveryMinutes;
            var scheduled = ReminderSchedule.Find(tab.Document.Id);
            if (scheduled is not null &&
                scheduled.EveryMinutes == every &&
                scheduled.NextOn > current &&
                tab.View.ApplyExternalReminder(scheduled.NextOn, every))
            {
                PersistExternalReminder(tab);
                continue;
            }

            if (scheduled is null &&
                every <= 0 &&
                ReminderSchedule.WasDelivered(tab.Document.Id, current) &&
                tab.View.ApplyExternalReminder(null, 0))
            {
                PersistExternalReminder(tab);
            }
        }
    }

    private void PersistExternalReminder(ChecklistTab tab)
    {
        tab.Document.ReminderOn = tab.View.ReminderOn;
        tab.Document.ReminderEveryMinutes = tab.View.ReminderEveryMinutes;
        tab.EditVersion++;
        tab.IsDirty = true;
        ScheduleAutoSave(tab);
    }

    private void PublishReminder(ChecklistTab tab)
    {
        if (tab.Document.IsCompleted || tab.View.ReminderOn is null)
        {
            ReminderSchedule.RemoveSlot(tab.Document.Id, string.Empty);
            return;
        }

        ReminderSchedule.Upsert(new ScheduledReminder
        {
            DocumentId = tab.Document.Id,
            Title = ChecklistTitle(tab),
            FilePath = tab.FilePath,
            NextOn = tab.View.ReminderOn.Value,
            EveryMinutes = tab.View.ReminderEveryMinutes,
            Silent = tab.View.ReminderSilent
        });
    }

    private void PublishDeadlineReminders(ChecklistTab tab)
    {
        var wanted = new List<ScheduledReminder>();
        AddDeadlineReminder(
            wanted,
            tab,
            ScheduledReminder.DeadlineSlot,
            ChecklistTitle(tab),
            tab.View.Deadline,
            tab.View.DeadlineMinute,
            tab.View.NotifyBeforeMinutes,
            tab.View.DeadlineSilent);

        foreach (var item in tab.View.Items)
        {
            if (item.IsCompleted || item.IsSkipped)
            {
                ReminderSchedule.DismissSlot(tab.Document.Id, ScheduledReminder.ItemSlot(item.Id));
                continue;
            }

            if (item.Deadline is null || item.NotifyBeforeMinutes is null)
                continue;

            var title = string.IsNullOrWhiteSpace(item.Title) ? ChecklistTitle(tab) : item.Title.Trim();
            AddDeadlineReminder(
                wanted,
                tab,
                ScheduledReminder.ItemSlot(item.Id),
                title,
                item.Deadline,
                item.DeadlineMinute,
                item.NotifyBeforeMinutes,
                item.DeadlineSilent);
        }

        ReminderSchedule.ReplaceAttached(tab.Document.Id, wanted);
    }

    private static void AddDeadlineReminder(
        List<ScheduledReminder> wanted,
        ChecklistTab tab,
        string slot,
        string title,
        DateOnly? deadline,
        int? minuteOfDay,
        int? minutesBefore,
        bool silent)
    {
        if (deadline is not { } date || minutesBefore is not int lead)
            return;

        var when = DeadlineValue.Instant(date, minuteOfDay).AddMinutes(-Math.Max(0, lead));
        wanted.Add(new ScheduledReminder
        {
            DocumentId = tab.Document.Id,
            Slot = slot,
            Title = title,
            Detail = DeadlineValue.NotifyText(lead),
            FilePath = tab.FilePath,
            NextOn = when,
            EveryMinutes = 0,
            Silent = silent
        });
    }

    private static string ChecklistTitle(ChecklistTab tab) =>
        string.IsNullOrWhiteSpace(tab.Document.Title) ? "Checklist" : tab.Document.Title.Trim();

    private void RefreshNotificationBell()
    {
        ApplyNotificationBadge();
        UpdateOpenNotificationList();
    }

    private void ApplyNotificationBadge()
    {
        var unseen = ReminderSchedule.UnseenCount();
        ReminderBadge.IsVisible = unseen > 0;
        if (unseen > 0)
            ReminderBell.Classes.Add("due");
        else
            ReminderBell.Classes.Remove("due");
    }

    private void ReminderBell_Opened(object? sender, EventArgs e)
    {
        _notificationListOpen = true;
        _openNoticeKeys = null;
        ReminderSchedule.MarkAllSeen();
        ApplyNotificationBadge();
        FillNotificationList();
    }

    private void ReminderBell_Closed(object? sender, EventArgs e)
    {
        _notificationListOpen = false;
        _openNoticeKeys = null;
    }

    private void UpdateOpenNotificationList()
    {
        if (!_notificationListOpen)
            return;

        var notices = ReminderSchedule.Notices();
        var keys = string.Join("\n", notices.Select(notice => notice.Key));
        if (keys == _openNoticeKeys)
            return;

        if (notices.Any(notice => !notice.Seen))
        {
            ReminderSchedule.MarkAllSeen();
            ApplyNotificationBadge();
        }

        FillNotificationList();
    }

    private void ClearAllNotifications_Click(object? sender, RoutedEventArgs e)
    {
        ReminderSchedule.DismissAll();
        RefreshNotificationBell();
    }

    private void FillNotificationList()
    {
        ReminderList.Children.Clear();
        var notices = ReminderSchedule.Notices();
        _openNoticeKeys = string.Join("\n", notices.Select(notice => notice.Key));
        ClearAllNotificationsButton.IsVisible = notices.Count > 0;
        if (notices.Count == 0)
        {
            ReminderList.Children.Add(new TextBlock
            {
                Text = "No notifications",
                Foreground = AppearanceService.Brush("TextSubtleBrush"),
                Margin = new Avalonia.Thickness(8, 8, 8, 6)
            });
            return;
        }

        foreach (var notice in notices)
        {
            var when = notice.DeliveredOn.Kind == DateTimeKind.Utc
                ? notice.DeliveredOn.ToLocalTime()
                : notice.DeliveredOn;
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Height = 52,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var open = new Button
            {
                Classes = { "notify-row" },
                Height = 52,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Classes = { "notify-title" },
                            Text = notice.Title
                        },
                        new TextBlock
                        {
                            Classes = { "notify-meta" },
                            Text = $"{when.ToString("dd MMM yyyy, h:mm tt", CultureInfo.InvariantCulture)} · {notice.Detail}"
                        }
                    }
                }
            };
            open.Click += (_, _) =>
            {
                ReminderBell.Flyout?.Hide();
                OpenReminderNotice(notice);
            };

            var dismiss = new Button
            {
                Classes = { "notify-dismiss" },
                Content = "×",
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(4, 0, 0, 0)
            };
            var key = notice.Key;
            dismiss.Click += (_, _) =>
            {
                ReminderSchedule.Dismiss(key);
                RefreshNotificationBell();
            };

            Grid.SetColumn(dismiss, 1);
            row.Children.Add(open);
            row.Children.Add(dismiss);
            ReminderList.Children.Add(row);
        }
    }

    private void OpenReminderNotice(ReminderNotice notice) =>
        OpenFromNotification(notice.DocumentId, notice.FilePath);

    internal void OpenFromNotification(Guid documentId, string? filePath)
    {
        BringToFront();
        if (documentId != Guid.Empty)
        {
            var open = _tabs.FirstOrDefault(tab => tab.Document.Id == documentId);
            if (open is not null)
            {
                ActivateTab(open);
                return;
            }

            if (_projects.Find(documentId) is { } project)
            {
                OpenProject(project);
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (_fileOperationInProgress)
            {
                _pendingFileOpens.Enqueue(filePath);
                return;
            }

            _ = RunFileOperation(() => OpenChecklistPathAsync(filePath));
            return;
        }

        ShowToast("That checklist isn't open.");
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        MacNotificationCenter.ActivateApp();
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastHost.IsVisible = true;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _toastTimer.Tick += OnToastTick;
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer?.Stop();
        ToastHost.IsVisible = false;
    }

    private void OnToastTick(object? sender, EventArgs e)
    {
        if (sender is DispatcherTimer timer)
        {
            timer.Stop();
            timer.Tick -= OnToastTick;
        }

        ToastHost.IsVisible = false;
    }

    private ChecklistTab? FindTabByPath(string path)
    {
        var fullPath = NormalizePath(path);
        if (fullPath is null)
            return null;

        return _tabs.FirstOrDefault(tab =>
            string.Equals(NormalizePath(tab.FilePath), fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizePath(string? path)
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

    private static string? LocalPath(IStorageFile file)
    {
        var path = file.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            return path;

        if (file.Path.IsAbsoluteUri && file.Path.IsFile)
            return file.Path.LocalPath;

        return null;
    }

    private static string SuggestedFileName(string title) =>
        SanitizeFileStem(title) + ".superdev.json";

    private static string SuggestedCsvName(string title) =>
        SanitizeFileStem(title) + ".csv";

    private static string ChecklistFileName(ChecklistTab tab) =>
        string.IsNullOrWhiteSpace(tab.FilePath) ? string.Empty : Path.GetFileName(tab.FilePath);

    private static string SanitizeFileStem(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Trim()
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray()).Trim();

        return cleaned.Length == 0 ? "Checklist" : cleaned;
    }

    private static string EnsureCsvExtension(string path) =>
        path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? path : path + ".csv";

    private static string EnsureChecklistExtension(string path)
    {
        const string extension = ".superdev.json";

        while (path.EndsWith(extension + extension, StringComparison.OrdinalIgnoreCase))
            path = path[..^extension.Length];

        if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            return path;

        if (path.EndsWith(extension + ".json", StringComparison.OrdinalIgnoreCase))
            return path[..^".json".Length];

        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return string.Concat(path.AsSpan(0, path.Length - ".json".Length), extension);

        return path + extension;
    }

    private async Task<ClosePromptResult> PromptUnsavedAsync(string title)
    {
        var dialog = new StyledDialog(
            "Save changes?",
            $"\"{title}\" has unsaved changes.",
            "Save",
            "Cancel",
            "Discard");

        return await dialog.ShowDialog<ClosePromptResult>(this);
    }

    private async Task<bool> ConfirmAsync(
        string title,
        string message,
        string primary,
        string secondary,
        bool danger = false)
    {
        var dialog = new StyledDialog(title, message, primary, secondary, danger);
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task ShowMessage(string message)
    {
        var dialog = new StyledDialog("Super Dev", message);
        await dialog.ShowDialog(this);
    }

    private void BindUpdateSettings()
    {
        UpdateVersionText.Text = "Version " + UpdateService.CurrentText;
        UpdateStatusText.Text = "Check for a newer release.";
        SyncUpdateOptions();
        AutoCheckBox.IsCheckedChanged += (_, _) =>
        {
            if (_syncingUpdateOptions)
                return;

            UpdateService.CheckAutomatically = AutoCheckBox.IsChecked == true;
            SyncUpdateOptions();
        };
        AutoUpgradeBox.IsCheckedChanged += (_, _) =>
        {
            if (_syncingUpdateOptions)
                return;

            UpdateService.InstallAutomatically = AutoUpgradeBox.IsChecked == true;
            SyncUpdateOptions();
        };
    }

    private void SyncUpdateOptions()
    {
        _syncingUpdateOptions = true;
        AutoCheckBox.IsChecked = UpdateService.CheckAutomatically;
        AutoUpgradeBox.IsChecked = UpdateService.InstallAutomatically;
        _syncingUpdateOptions = false;
    }

    private async void CheckForUpdates_Click(object? sender, RoutedEventArgs e) =>
        await RunUpdateCheckAsync(interactive: true, automatic: false);

    private async void InstallUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy || _availableUpdate is not { IsNewer: true } update)
            return;

        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
        {
            ExternalLink.TryOpen(update.ReleaseUrl);
            return;
        }

        await WithUpdateAsync(() => ShowUpdateAsync(update, startDownload: true, confirmWhenReady: true));
    }

    private async Task RunUpdateCheckAsync(bool interactive, bool automatic)
    {
        if (_updateBusy)
            return;

        await WithUpdateAsync(async () =>
        {
            SetUpdateStatus("Checking for updates…");
            var result = await UpdateService.CheckAsync();
            ApplyCheckResult(result);

            if (result.Error is not null)
            {
                if (interactive)
                    await ShowMessage(result.Error);
                return;
            }

            if (!result.IsNewer || result.LatestText is null)
            {
                if (interactive)
                    await ShowMessage($"Super Dev {UpdateService.CurrentText} is up to date.");
                return;
            }

            var installNow = automatic && result.CanInstall;
            await ShowUpdateAsync(result, startDownload: installNow, confirmWhenReady: !installNow);
        });
    }

    private async Task WithUpdateAsync(Func<Task> action)
    {
        if (_updateBusy)
            return;

        _updateBusy = true;
        SetUpdateControlsEnabled(false);
        try
        {
            await action();
        }
        finally
        {
            _updateBusy = false;
            if (!_restartingForUpdate)
                SetUpdateControlsEnabled(true);
        }
    }

    private async Task ShowUpdateAsync(UpdateCheckResult update, bool startDownload, bool confirmWhenReady)
    {
        var dialog = new UpdateAvailableWindow(update, startDownload, confirmWhenReady, SetUpdateStatus);
        var outcome = await dialog.ShowDialog<UpdatePromptResult?>(this);
        if (outcome?.Prepared is null)
        {
            if (outcome?.OpenRelease == true)
                ExternalLink.TryOpen(update.ReleaseUrl);
            return;
        }

        if (!outcome.Install)
        {
            try
            {
                SetUpdateStatus("Opening the download…");
                outcome.Prepared.Apply();
                SetUpdateStatus($"Super Dev {update.LatestText} is downloaded.");
            }
            catch (Exception)
            {
                outcome.Prepared.Cancel();
                SetUpdateStatus("Couldn't open the download.");
            }

            return;
        }

        if (!await PrepareToRestartAsync(promptForNewFiles: outcome.PromptForNewFiles))
        {
            outcome.Prepared.Cancel();
            SetUpdateStatus(outcome.PromptForNewFiles
                ? StatusFor(update)
                : $"Super Dev {update.LatestText} is available. Save open checklists, then install it.");
            return;
        }

        try
        {
            SetUpdateStatus("Restarting…");
            ShowToast(update.Kind == UpdatePackageKind.WindowsMsix
                ? "Opening the installer…"
                : "Restarting to install the update…");
            outcome.Prepared.Apply();
        }
        catch (Exception)
        {
            outcome.Prepared.Cancel();
            SetUpdateStatus("Couldn't install the update.");
            return;
        }

        _restartingForUpdate = true;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Close();
    }

    private void ApplyCheckResult(UpdateCheckResult result)
    {
        _availableUpdate = result.IsNewer ? result : null;
        if (result.Error is not null)
        {
            SetUpdateStatus(result.Error);
            InstallUpdateButton.IsVisible = false;
            return;
        }

        if (!result.IsNewer || result.LatestText is null)
        {
            SetUpdateStatus($"Super Dev {UpdateService.CurrentText} is up to date.");
            InstallUpdateButton.IsVisible = false;
            return;
        }

        SetUpdateStatus(StatusFor(result));
        var canDownload = !string.IsNullOrWhiteSpace(result.DownloadUrl);
        InstallUpdateButton.Content = canDownload ? "Download" : "View release";
        InstallUpdateButton.IsVisible = canDownload || !string.IsNullOrWhiteSpace(result.ReleaseUrl);
    }

    private static string StatusFor(UpdateCheckResult result)
    {
        var status = $"Super Dev {result.LatestText} is available.";
        if (!string.IsNullOrWhiteSpace(result.Note))
            status += " " + result.Note;
        return status;
    }

    private async Task<bool> PrepareToRestartAsync(bool promptForNewFiles)
    {
        foreach (var tab in _tabs.ToList())
        {
            tab.AutoSaveTimer?.Stop();
            while (tab.AutoSaveTask is { } pending)
                await pending;

            if (!string.IsNullOrWhiteSpace(tab.FilePath))
            {
                if (tab.IsDirty && !await WriteDocumentAsync(tab, tab.FilePath, announce: false))
                    return false;
                continue;
            }

            if (!tab.IsDirty)
                continue;

            if (!promptForNewFiles)
                return false;

            var choice = await PromptUnsavedAsync(tab.Document.Title);
            if (choice == ClosePromptResult.Cancel)
                return false;

            if (choice == ClosePromptResult.Save && !await SaveAsync(tab))
                return false;
        }

        return true;
    }

    private void SetUpdateStatus(string message) => UpdateStatusText.Text = message;

    private void SetUpdateControlsEnabled(bool enabled)
    {
        CheckUpdatesButton.IsEnabled = enabled;
        InstallUpdateButton.IsEnabled = enabled;
    }

    private sealed class ChecklistTab(
        ChecklistDocument document,
        ChecklistView view,
        Border headerContainer,
        TextBlock titleText,
        TextBlock projectLabel,
        Control completionMark,
        Button pinButton)
    {
        public ChecklistDocument Document { get; } = document;
        public ChecklistView View { get; } = view;
        public Border HeaderContainer { get; } = headerContainer;
        public TextBlock TitleText { get; } = titleText;
        public TextBlock ProjectLabel { get; } = projectLabel;
        public Control CompletionMark { get; } = completionMark;
        public Button PinButton { get; } = pinButton;
        public bool IsPinned { get; set; }
        public Guid? OpenedFromProjectId { get; set; }
        public string? FilePath { get; set; }
        public bool IsDirty { get; set; }
        public bool IsSaving { get; set; }
        public int EditVersion { get; set; }
        public DispatcherTimer? AutoSaveTimer { get; set; }
        public Task? AutoSaveTask { get; set; }
    }
}
