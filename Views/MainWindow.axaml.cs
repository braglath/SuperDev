using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SuperDev.Models;
using SuperDev.Services;

namespace SuperDev.Views;

public partial class MainWindow : Window
{
    private readonly ChecklistTemplateService _checklistService = new();
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
    private bool _slideClock;
    private int _slideEpoch;
    private readonly Dictionary<Border, double> _slideTarget = new();
    private bool _fileOperationInProgress;
    private readonly Queue<string> _pendingFileOpens = new();
    private bool _settingsOpen;
    private bool _settingsOverChecklist;
    private bool _settingsOverReminders;
    private bool _remindersOpen;
    private readonly HashSet<Key> _keysDown = new();
    private string? _lastShortcutId;
    private long _lastShortcutAt;
    private NativeMenuItem? _saveMenuItem;
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
    private const int AutoSaveDelayMilliseconds = 800;
    private static readonly TimeSpan SaveToastInterval = TimeSpan.FromMinutes(30);

    public MainWindow()
    {
        InitializeComponent();
        BindChecklistMenuItems();
        ConfigureTitleChrome();
        BuildShortcutSettings();
        AddHandler(KeyDownEvent, ShortcutKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, ShortcutKeyUp, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _keysDown.Clear();
        UpdateManualSaveVisibility();
        RefreshRecentList();
        BuildTemplateCards(null);
        ToolTip.SetTip(NewTabButton, AppShortcuts.WithShortcut(AppShortcuts.NewChecklist));
        ToolTip.SetTip(OpenChecklistButton, AppShortcuts.WithShortcut(AppShortcuts.OpenChecklist));
        StartReminderTimer();
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
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var tab in _tabs)
        {
            if (string.IsNullOrWhiteSpace(tab.FilePath))
                ReminderSchedule.Remove(tab.Document.Id);
        }

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
                    case "Open…":
                        ApplyMenuGesture(item, AppShortcuts.OpenChecklist);
                        break;
                    case "Save":
                        _saveMenuItem = item;
                        ApplyMenuGesture(item, AppShortcuts.Save);
                        break;
                    case "Save As…":
                        ApplyMenuGesture(item, AppShortcuts.SaveAs);
                        break;
                    case "Keyboard Shortcuts":
                        ApplyMenuGesture(item, AppShortcuts.Settings);
                        break;
                    case "Add Check":
                        _addCheckMenuItem = item;
                        ApplyMenuGesture(item, AppShortcuts.AddItem);
                        break;
                    case "Save as template":
                        _saveTemplateMenuItem = item;
                        break;
                    case "Rearrange…":
                        _rearrangeMenuItem = item;
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

    private Control CreateTemplateCard(ChecklistTemplate template, bool canEdit)
    {
        var button = new Button
        {
            Classes = { "template" },
            Tag = template.Id,
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = template.Icon,
                        Classes = { "template-icon" }
                    },
                    new TextBlock
                    {
                        Text = template.Title,
                        Classes = { "template-title" }
                    }
                }
            }
        };
        button.Click += WorkflowButton_Click;
        _templateButtons.Add(button);

        var host = new Grid
        {
            Width = 148,
            Height = 84,
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
            Content = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "＋",
                        Classes = { "template-icon" }
                    },
                    new TextBlock
                    {
                        Text = "New template",
                        Classes = { "template-title" }
                    }
                }
            }
        };
        button.Click += NewTemplate_Click;

        return new Grid
        {
            Width = 148,
            Height = 84,
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

    private void Repository_TextChanged(object? sender, TextChangedEventArgs e)
    {
        OpenRepositoryButton.IsEnabled = ExternalLink.CanOpen(RepositoryTextBox.Text);
    }

    private async void OpenRepository_Click(object? sender, RoutedEventArgs e)
    {
        if (!ExternalLink.TryOpen(RepositoryTextBox.Text))
            await ShowMessage("That repository link could not be opened.");
    }

    private void PullRequest_TextChanged(object? sender, TextChangedEventArgs e)
    {
        OpenPullRequestButton.IsEnabled = ExternalLink.CanOpen(PullRequestTextBox.Text);
    }

    private async void OpenPullRequest_Click(object? sender, RoutedEventArgs e)
    {
        if (!ExternalLink.TryOpen(PullRequestTextBox.Text))
            await ShowMessage("That PR link could not be opened.");
    }

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
            BranchName = BranchNameTextBox.Text?.Trim() ?? string.Empty,
            RepositoryUrl = RepositoryTextBox.Text?.Trim() ?? string.Empty,
            PullRequestUrl = PullRequestTextBox.Text?.Trim() ?? string.Empty,
            WorkType = template.WorkType,
            TemplateName = template.Title,
            CreatedOn = now,
            UpdatedOn = now,
            IsCompleted = false,
            Items = template.Id == "blank" ? [] : _checklistService.CreateItems(template)
        };

        OpenDocumentTab(document, filePath: null, isDirty: true);
        ClearCreateForm();
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

            await _fileService.SaveAsync(path, document, replaceDifferentDocument.Value);
            RememberChecklist(path, document);
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
        BranchNameTextBox.Text = string.Empty;
        RepositoryTextBox.Text = string.Empty;
        PullRequestTextBox.Text = string.Empty;
        _selectedTemplateId = null;
        ClearTemplateSelection();
    }

    private void OpenDocumentTab(
        ChecklistDocument document,
        string? filePath,
        bool isDirty)
    {
        var view = new ChecklistView(
            document.WorkType,
            document.TemplateName,
            document.Title,
            document.BranchName ?? string.Empty,
            document.RepositoryUrl ?? string.Empty,
            document.PullRequestUrl ?? string.Empty,
            document.Deadline,
            document.DeadlineMinute,
            document.NotifyBeforeMinutes,
            document.DeadlineSilent,
            document.ReminderOn,
            document.ReminderEveryMinutes,
            document.ReminderSilent,
            document.Items);

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
                Stroke = new SolidColorBrush(Color.Parse("#4ADE80")),
                StrokeThickness = 1.8,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Data = Geometry.Parse("M1,5.5 L4.2,8.5 L10,2")
            }
        };

        ToolTip.SetTip(completionMark, "Completed");

        var titleRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children = { completionMark, titleText }
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

        var tab = new ChecklistTab(document, view, tabHeader, titleText, completionMark, pinButton)
        {
            FilePath = filePath,
            IsDirty = isDirty
        };

        view.SetManualSaveVisible(string.IsNullOrWhiteSpace(filePath));
        view.SetDocumentCompleted(document.IsCompleted);
        view.DocumentEdited += (_, _) => OnDocumentEdited(tab);
        view.DocumentCompleted += (_, _) => OnDocumentCompleted(tab);
        view.DocumentReopened += (_, _) => OnDocumentReopened(tab);
        view.SaveRequested += (_, _) => _ = RunFileOperation(() => SaveAsync(tab));
        view.SaveAsRequested += (_, _) => _ = RunFileOperation(() => SaveAsAsync(tab));
        view.SaveAsTemplateRequested += (_, _) => _ = SaveTabAsTemplateAsync(tab);
        view.CopyRequested += (_, _) => _ = RunFileOperation(() => CopyChecklistAsync(tab));
        view.SubtasksRequired += (_, message) => ShowToast(message);
        view.LinkedSubtaskOpenRequested += (_, subtask) => _ = OpenLinkedSubtaskAsync(subtask);
        view.RequestLink = owner => PickLinkedChecklistAsync(tab, owner);

        tabHeader.PointerPressed += (_, e) => BeginTabDrag(tab, e);
        tabHeader.PointerMoved += (_, e) => UpdateTabDrag(tab, e);
        tabHeader.PointerReleased += (_, e) => EndTabDrag(tab, e);
        tabHeader.PointerCaptureLost += (_, _) => EndTabDrag(tab, null);

        closeButton.Click += (_, _) => _ = CloseTabFromUserAsync(tab);
        pinButton.Click += (_, _) => TogglePin(tab);

        _tabs.Add(tab);
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
            Stroke = new SolidColorBrush(Color.Parse(filled ? "#FFFFFF" : "#F8FAFC")),
            Fill = new SolidColorBrush(Color.Parse(filled ? "#FFFFFF" : "#00000000")),
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
        var index = _tabs.IndexOf(tab);

        if (index < 0)
            return;

        tab.AutoSaveTimer?.Stop();
        TabsPanel.Children.Remove(tab.HeaderContainer);
        _tabs.Remove(tab);
        UpdatePinVisibility();

        if (_activeTab != tab)
        {
            TabBar.IsVisible = _tabs.Count > 0;
            RefreshRecentList();
            if (string.IsNullOrWhiteSpace(tab.FilePath))
                ReminderSchedule.Remove(tab.Document.Id);
            RefreshNotificationBell();
            return;
        }

        if (_tabs.Count > 0)
        {
            var nextIndex = Math.Min(index, _tabs.Count - 1);
            ActivateTab(_tabs[nextIndex]);
        }
        else
        {
            _activeTab = null;
            WorkspaceContent.Content = null;
            WorkspaceContent.IsVisible = false;
            TabBar.IsVisible = false;
            HomePanel.IsVisible = true;
            SetChecklistActionsEnabled(false);
        }

        RefreshRecentList();
        if (string.IsNullOrWhiteSpace(tab.FilePath))
            ReminderSchedule.Remove(tab.Document.Id);
        RefreshNotificationBell();
    }

    private void BeginTabDrag(ChecklistTab tab, PointerPressedEventArgs e)
    {
        if (IsInsideButton(e.Source))
            return;

        if (!e.GetCurrentPoint(tab.HeaderContainer).Properties.IsLeftButtonPressed)
            return;

        ActivateTab(tab);
        if (tab.IsPinned || _tabs.Count(open => !open.IsPinned) < 2)
            return;

        _dragTab = tab;
        _dragStart = e.GetPosition(TabsPanel);
        _dragOriginX = tab.HeaderContainer.Bounds.X;
        _dragTarget = _tabs.IndexOf(tab);
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
            tab.HeaderContainer.Cursor = new Cursor(StandardCursorType.SizeWestEast);
        }

        var grab = _dragStart.X - _dragOriginX;
        var visualX = ClampDraggedVisualX(tab, position.X - grab);
        var target = DropIndex(visualX + grab, tab);
        if (target != _dragTarget)
        {
            _dragTarget = target;
            SlideTabsForDrop(tab, target);
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
        _dragTab = null;
        _tabDragging = false;
        _dragTarget = -1;
        tab.HeaderContainer.Classes.Remove("dragging");
        tab.HeaderContainer.ClearValue(Avalonia.Input.InputElement.CursorProperty);

        if (e is not null)
            e.Pointer.Capture(null);

        if (dropped)
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
        translate.X = visualX - header.Bounds.X;
        translate.Y = -2;
    }

    private void CommitTabDrop(ChecklistTab dragging, int target)
    {
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
        var show = _tabs.Count > 1;
        foreach (var tab in _tabs)
            tab.PinButton.IsVisible = show;

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
        _activeTab = tab;

        foreach (var openTab in _tabs)
            openTab.HeaderContainer.Classes.Remove("selected");

        tab.HeaderContainer.Classes.Add("selected");

        _settingsOpen = false;
        _settingsOverChecklist = false;
        _settingsOverReminders = false;
        _remindersOpen = false;
        RemindersButton.Classes.Remove("selected");
        RemindersPanel.IsVisible = false;
        SettingsPanel.IsVisible = false;
        WorkspaceContent.Content = tab.View;
        WorkspaceContent.IsVisible = true;
        TabBar.IsVisible = true;
        HomePanel.IsVisible = false;
        SetChecklistActionsEnabled(true);
        UpdateManualSaveVisibility();
    }

    private void NewTab_Click(
        object? sender,
        RoutedEventArgs e)
    {
        ShowHomeScreen();
    }

    private void ShowHomeScreen()
    {
        _settingsOpen = false;
        _settingsOverChecklist = false;
        _settingsOverReminders = false;
        _remindersOpen = false;
        RemindersButton.Classes.Remove("selected");
        SettingsPanel.IsVisible = false;
        RemindersPanel.IsVisible = false;
        WorkspaceContent.IsVisible = false;
        WorkspaceContent.Content = null;

        HomePanel.IsVisible = true;
        TabBar.IsVisible = _tabs.Count > 0;
        SetChecklistActionsEnabled(false);
        UpdateManualSaveVisibility();
        RefreshRecentList();
    }

    private void SetChecklistActionsEnabled(bool enabled)
    {
        var editable = enabled && ActiveTab()?.Document.IsCompleted != true;
        if (_addCheckMenuItem is not null)
            _addCheckMenuItem.IsEnabled = editable;

        WindowAddCheckMenuItem.IsEnabled = editable;

        var hasItems = enabled && ActiveTab()?.View.Items.Count > 0;
        var canRearrange = editable && ActiveTab()?.View.Items.Count > 1;
        SetItemActionVisible(_copyToMenuItem, WindowCopyMenuItem, editable && hasItems);
        SetItemActionVisible(_deleteChecksMenuItem, WindowDeleteMenuItem, editable && hasItems);
        SetItemActionVisible(_saveTemplateMenuItem, WindowSaveTemplateMenuItem, editable);
        SetItemActionVisible(_rearrangeMenuItem, WindowRearrangeMenuItem, canRearrange);
        UpdatePinMenuItem();
    }

    private void UpdatePinMenuItem()
    {
        var checklistOpen = !HomePanel.IsVisible && !SettingsPanel.IsVisible && !RemindersPanel.IsVisible && ActiveTab() is not null;
        var pinLabel = ActiveTab()?.IsPinned == true ? "Unpin Tab" : "Pin Tab";
        if (_pinTabMenuItem is not null)
            _pinTabMenuItem.Header = pinLabel;
        WindowPinMenuItem.Header = pinLabel;
        SetItemActionVisible(_pinTabMenuItem, WindowPinMenuItem, checklistOpen && _tabs.Count > 1);
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
        // Minimize, maximize, full screen, and close. The full-screen button is the extra one that covered the bell.
        const double captionButtons = 46 * 4 + 12;
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
                    "Open…" => AppShortcuts.OpenChecklist,
                    "Save" => AppShortcuts.Save,
                    "Save As…" => AppShortcuts.SaveAs,
                    "Add Check" => AppShortcuts.AddItem,
                    "Keyboard Shortcuts" => AppShortcuts.Settings,
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
            case "Open…":
                InvokeShortcut(AppShortcuts.OpenChecklist);
                break;
            case "Save":
                InvokeShortcut(AppShortcuts.Save);
                break;
            case "Save As…":
                InvokeShortcut(AppShortcuts.SaveAs);
                break;
            case "Reminders":
                ShowReminders();
                break;
            case "Add Check":
                InvokeShortcut(AppShortcuts.AddItem);
                break;
            case "Save as template":
                SaveAsTemplateMenu_Click(this, EventArgs.Empty);
                break;
            case "Rearrange…":
                RearrangeMenu_Click(this, EventArgs.Empty);
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
            case "Keyboard Shortcuts":
                InvokeShortcut(AppShortcuts.Settings);
                break;
        }
    }

    private void ShowSettings()
    {
        if (!_settingsOpen)
        {
            _settingsOverReminders = _remindersOpen;
            _settingsOverChecklist = !_remindersOpen && ActiveTab() is not null;
        }

        _settingsOpen = true;
        _remindersOpen = false;
        RemindersButton.Classes.Remove("selected");
        HomePanel.IsVisible = false;
        WorkspaceContent.IsVisible = false;
        RemindersPanel.IsVisible = false;
        SettingsPanel.IsVisible = true;
        TabBar.IsVisible = _tabs.Count > 0;
        UpdateManualSaveVisibility();
    }

    private void CloseSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (_settingsOverReminders)
            ShowReminders();
        else if (_settingsOverChecklist && _activeTab is not null && _tabs.Contains(_activeTab))
            ActivateTab(_activeTab);
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
        if (_activeTab is not null && _tabs.Contains(_activeTab))
            ActivateTab(_activeTab);
        else
            ShowHomeScreen();
    }

    private void ShowReminders()
    {
        _settingsOpen = false;
        _settingsOverChecklist = false;
        _settingsOverReminders = false;
        _remindersOpen = true;
        foreach (var openTab in _tabs)
            openTab.HeaderContainer.Classes.Remove("selected");
        RemindersButton.Classes.Add("selected");
        HomePanel.IsVisible = false;
        SettingsPanel.IsVisible = false;
        WorkspaceContent.IsVisible = false;
        WorkspaceContent.Content = null;
        RemindersPanel.IsVisible = true;
        TabBar.IsVisible = _tabs.Count > 0;
        SetChecklistActionsEnabled(false);
        UpdateManualSaveVisibility();
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
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#F8FAFC")),
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = $"{ReminderKind(reminder)} · {when}{repeat}{silent}",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#94A3B8")),
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
            Background = new SolidColorBrush(Color.Parse("#0F172A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#1E293B")),
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

    private static string ReminderKind(ScheduledReminder reminder)
    {
        if (string.IsNullOrEmpty(reminder.Slot))
            return "Reminder";
        if (reminder.Slot == ScheduledReminder.DeadlineSlot)
            return "Deadline";
        return "Check";
    }

    private static Button ReminderAction(string content) => new()
    {
        Content = content,
        Height = 34,
        Padding = new Avalonia.Thickness(12, 0),
        Background = new SolidColorBrush(Color.Parse("#172033")),
        Foreground = new SolidColorBrush(Color.Parse("#E2E8F0")),
        BorderBrush = new SolidColorBrush(Color.Parse("#334155")),
        BorderThickness = new Avalonia.Thickness(1),
        CornerRadius = new Avalonia.CornerRadius(8),
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    private void BuildShortcutSettings()
    {
        var onMac = OperatingSystem.IsMacOS();
        var muted = new SolidColorBrush(Color.Parse("#64748B"));
        var line = new SolidColorBrush(Color.Parse("#1E293B"));

        foreach (ShortcutGroup group in Enum.GetValues<ShortcutGroup>())
        {
            var shortcuts = AppShortcuts.All.Where(item => item.Group == group).ToList();
            if (shortcuts.Count == 0)
                continue;

            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(new TextBlock
            {
                Text = AppShortcuts.GroupTitle(group),
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#CBD5E1"))
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
                        FontSize = 14,
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
                Background = new SolidColorBrush(Color.Parse("#0F172A")),
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
            FontSize = 12,
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
            Background = new SolidColorBrush(Color.Parse(current ? "#172F55" : "#172033")),
            BorderBrush = new SolidColorBrush(Color.Parse(current ? "#3B82F6" : "#334155")),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(8, 4),
            Margin = new Avalonia.Thickness(8, 8),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse(current ? "#DBEAFE" : "#E2E8F0"))
            }
        };

    private void ShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        var firstPress = _keysDown.Add(e.Key);
        if (e.Handled || !CanUseShortcuts())
            return;

        var shortcut = AppShortcuts.Match(e, ShortcutScope.Window);
        if (shortcut is null)
            return;

        e.Handled = true;
        if (!firstPress)
            return;

        InvokeShortcut(shortcut.Id);
    }

    private void ShortcutKeyUp(object? sender, KeyEventArgs e) =>
        _keysDown.Remove(e.Key);

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
                ShowHomeScreen();
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
            case AppShortcuts.Settings:
                ShowSettings();
                break;
            case AppShortcuts.CloseTab:
                if (ActiveTab() is { } closing)
                    _ = RunFileOperation(() => CloseTabAsync(closing));
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
                    if (_settingsOpen || _remindersOpen)
                        ActivateTab(searching);
                    searching.View.FocusSearch();
                }
                break;
            case AppShortcuts.AddItem:
                if (ActiveTab() is { } adding && !adding.Document.IsCompleted)
                {
                    if (_settingsOpen || _remindersOpen)
                        ActivateTab(adding);
                    adding.View.AddCheck();
                }
                break;
        }
    }

    private void SelectTabRelative(int delta)
    {
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

    private void KeyboardShortcutsMenu_Click(object? sender, EventArgs e) =>
        InvokeShortcut(AppShortcuts.Settings);

    private void CopyMenu_Click(object? sender, EventArgs e)
    {
        if (ActiveTab() is not { } tab || tab.View.Items.Count == 0)
            return;

        _ = RunFileOperation(() => CopyChecklistAsync(tab));
    }

    private void AddCheckMenu_Click(object? sender, EventArgs e) =>
        InvokeShortcut(AppShortcuts.AddItem);

    private void SaveAsTemplateMenu_Click(object? sender, EventArgs e) =>
        ActiveTab()?.View.SaveAsTemplate();

    private void RearrangeMenu_Click(object? sender, EventArgs e) =>
        ActiveTab()?.View.RearrangeChecks();

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
        if (HomePanel.IsVisible || (_settingsOpen && !_settingsOverChecklist))
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
        RememberChecklist(path, tab.Document);
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
            checks.Select(CopyItemLabel).ToList(),
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
        var added = destination.View.AppendCopies(selected);
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
        var copy = new ChecklistDocument
        {
            FormatVersion = ChecklistDocument.CurrentFormatVersion,
            Id = Guid.NewGuid(),
            Title = source.View.WorkTitle,
            BranchName = source.View.BranchName,
            RepositoryUrl = source.View.RepositoryUrl,
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
            CreatedOn = now,
            UpdatedOn = now,
            IsCompleted = items.Count == source.View.Items.Count &&
                          source.Document.IsCompleted &&
                          !source.View.HasIncompleteRequiredChecks(),
            Items = items.Select(CopyItem).ToList()
        };

        var replaceDifferentDocument = await PrepareOverwriteAsync(path, copy.Id);
        if (replaceDifferentDocument is null)
            return;

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

        RememberChecklist(path, copy);
        OpenDocumentTab(copy, path, isDirty: false);
        await ShowMessage($"Saved a copy of \"{copy.Title}\".");
    }

    private static string CopyItemLabel(ChecklistItem item) =>
        string.IsNullOrWhiteSpace(item.Detail)
            ? item.Title
            : $"{item.Title} — {item.Detail}";

    private static string CopyTargetLabel(ChecklistTab tab)
    {
        if (string.IsNullOrWhiteSpace(tab.FilePath))
            return $"{tab.Document.Title} · Not saved yet";

        return $"{tab.Document.Title} · {Path.GetFileName(tab.FilePath)}";
    }

    private static ChecklistItem CopyItem(ChecklistItem item) =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = item.Title,
            Category = item.Category,
            Priority = item.Priority,
            Notes = item.Notes,
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
                    LinkedFilePath = subtask.LinkedFilePath
                })
                .ToList(),
            IsCompleted = item.IsCompleted
        };

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

    private void RememberChecklist(string path, ChecklistDocument document) =>
        RememberChecklist(path, document.Title, document.WorkType, document.IsCompleted);

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

    private void RefreshRecentList()
    {
        RecentItems.Children.Clear();

        var index = 0;
        foreach (var entry in _recentChecklists.Entries.Take(RecentChecklistService.MaxEntries))
        {
            var openTab = FindTabByPath(entry.Path);
            var missing = openTab is null && !File.Exists(entry.Path);
            var title = openTab?.Document.Title ?? entry.Title;
            var completed = openTab?.Document.IsCompleted ?? entry.IsCompleted;

            var detail = openTab is not null
                ? OpenTabDetail(openTab)
                : missing
                    ? "File missing"
                    : SavedChecklistDetail(entry);

            var titleText = new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#F8FAFC")),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
            };

            var detailText = new TextBlock
            {
                Text = detail,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse(missing ? "#F87171" : "#94A3B8")),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
            };

            var text = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Children = { titleText, detailText }
            };

            var content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            content.Children.Add(text);

            if (completed && !missing)
            {
                var mark = new TextBlock
                {
                    Text = "Completed",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#4ADE80")),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Avalonia.Thickness(12, 0, 0, 0)
                };
                Grid.SetColumn(mark, 1);
                content.Children.Add(mark);
            }

            var button = new Button
            {
                Classes = { "recent" },
                Tag = entry.Path,
                Content = content,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
            };
            ToolTip.SetTip(button, entry.Path);
            button.Click += Recent_Click;

            Grid.SetColumn(button, index % 2);
            Grid.SetRow(button, index / 2);
            RecentItems.Children.Add(button);
            index++;
        }

        RecentSection.IsVisible = RecentItems.Children.Count > 0;
    }

    private static string OpenTabDetail(ChecklistTab tab)
    {
        var detail = WorkflowLabel(tab.Document.WorkType) + " · Open";
        if (tab.IsDirty)
            detail += " · Unsaved changes";
        return detail;
    }

    private static string SavedChecklistDetail(RecentChecklistEntry entry) =>
        WorkflowLabel(entry.WorkType);

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
        tab.Document.BranchName = tab.View.BranchName;
        tab.Document.RepositoryUrl = tab.View.RepositoryUrl;
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
        tab.Document.Items = tab.View.Items.ToList();
    }

    private void OnDocumentEdited(ChecklistTab tab)
    {
        tab.EditVersion++;
        tab.IsDirty = true;
        tab.Document.Title = tab.View.WorkTitle;
        tab.Document.BranchName = tab.View.BranchName;
        tab.Document.RepositoryUrl = tab.View.RepositoryUrl;
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

        ToolTip.SetTip(tab.HeaderContainer, tip);
        ToolTip.SetTip(tab.TitleText, tip);
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
            if (item.IsCompleted)
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
                Foreground = new SolidColorBrush(Color.Parse("#64748B")),
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

    private static string SuggestedFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Trim()
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray()).Trim();

        if (cleaned.Length == 0)
            cleaned = "Checklist";

        return cleaned + ".superdev.json";
    }

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

    private sealed class ChecklistTab(
        ChecklistDocument document,
        ChecklistView view,
        Border headerContainer,
        TextBlock titleText,
        Control completionMark,
        Button pinButton)
    {
        public ChecklistDocument Document { get; } = document;
        public ChecklistView View { get; } = view;
        public Border HeaderContainer { get; } = headerContainer;
        public TextBlock TitleText { get; } = titleText;
        public Control CompletionMark { get; } = completionMark;
        public Button PinButton { get; } = pinButton;
        public bool IsPinned { get; set; }
        public string? FilePath { get; set; }
        public bool IsDirty { get; set; }
        public bool IsSaving { get; set; }
        public int EditVersion { get; set; }
        public DispatcherTimer? AutoSaveTimer { get; set; }
        public Task? AutoSaveTask { get; set; }
    }
}
