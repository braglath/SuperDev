using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SuperDev.Models;
using SuperDev.Services;
using SuperDev.Services.Activity;

namespace SuperDev.Views;

public partial class HistoryView : UserControl
{
    private readonly DispatcherTimer _searchTimer;
    private readonly List<WorkChoice> _workChoices = [];
    private readonly Dictionary<string, TextBlock> _stats = new();
    private bool _applying;
    private bool _showingAll;
    private string _projectSignature = string.Empty;
    private TrendGranularity _requestedGranularity = TrendGranularity.Daily;

    public HistoryView()
    {
        InitializeComponent();
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            if (!_applying)
                SearchChanged?.Invoke(this, EventArgs.Empty);
        };
        BuildWorkTypes();
        BuildStatCards();
        SizeChanged += (_, e) => ArrangeForWidth(e.NewSize.Width);
        ArrangeForWidth(Bounds.Width);
    }

    public event EventHandler? BackRequested;

    public event EventHandler? WeekRequested;

    public event EventHandler? MonthRequested;

    public event EventHandler? CustomPeriodRequested;

    public event EventHandler? CustomDatesChanged;

    public event EventHandler? GranularityChanged;

    public event EventHandler? ProjectChanged;

    public event EventHandler? SearchChanged;

    public event EventHandler? WorkTypeChanged;

    public event EventHandler? ViewAllRequested;

    public event EventHandler<CompletedTasklistEntry>? OpenRequested;

    public DateOnly? CustomStartDate => ReadDate(RangeStart);

    public DateOnly? CustomEndDate => ReadDate(RangeEnd);

    public string SearchText => SearchBox.Text?.Trim() ?? string.Empty;

    public HistoryProjectScope SelectedScope =>
        ProjectCombo.SelectedItem is ProjectChoice choice ? choice.Scope : HistoryProjectScope.All;

    public Guid? SelectedProjectId =>
        ProjectCombo.SelectedItem is ProjectChoice choice ? choice.ProjectId : null;

    public WorkType? SelectedWorkType =>
        WorkTypeCombo.SelectedItem is WorkChoice choice ? choice.WorkType : null;

    public TrendGranularity RequestedGranularity => _requestedGranularity;

    public void ShowRangeError(string? error)
    {
        RangeErrorText.Text = error ?? string.Empty;
        RangeErrorText.IsVisible = !string.IsNullOrWhiteSpace(error);
    }

    public void Apply(HistoryDashboard dashboard, HistoryViewState state)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        ArgumentNullException.ThrowIfNull(state);
        _applying = true;
        _showingAll = state.ShowingAllCompleted;
        try
        {
            SelectPeriod(WeekButton, state.Period == HistoryPeriodKind.ThisWeek);
            SelectPeriod(MonthButton, state.Period == HistoryPeriodKind.ThisMonth);
            SelectPeriod(CustomButton, state.Period == HistoryPeriodKind.Custom);
            CustomRangePanel.IsVisible = state.Period == HistoryPeriodKind.Custom;
            RangeText.Text = dashboard.Range.Label();
            ShowRangeError(state.RangeError);
            SetDate(RangeStart, state.CustomStart);
            SetDate(RangeEnd, state.CustomEnd);
            ApplyProjects(state);
            ApplyWorkType(state.WorkType);
            SetStat("CompletedTasklists", dashboard.CompletedTasklists);
            SetStat("StartedTasklists", dashboard.StartedTasklists);
            SetStat("InProgressTasklists", dashboard.InProgressTasklists);
            SetStat("PendingTasklists", dashboard.PendingTasklists);
            SetStat("ChecksCompleted", dashboard.ChecksCompleted);
            SetStat("ChecksReopened", dashboard.ChecksReopened);
            SetStat("PendingChecks", dashboard.PendingChecks);
            RateValue.Text = dashboard.CompletionRatePercent.ToString(CultureInfo.CurrentCulture) + "%";
            var rate = Math.Clamp(dashboard.CompletionRatePercent, 0, 100);
            RateBar.ColumnDefinitions[0].Width = new GridLength(rate, GridUnitType.Star);
            RateBar.ColumnDefinitions[1].Width = new GridLength(Math.Max(0, 100 - rate), GridUnitType.Star);
            SelectSmall(DailyButton, dashboard.Granularity == TrendGranularity.Daily);
            SelectSmall(WeeklyButton, dashboard.Granularity == TrendGranularity.Weekly);
            BuildTrend(dashboard);
            BuildCompleted(dashboard, state);
        }
        finally
        {
            _applying = false;
        }
    }

    private void BuildWorkTypes()
    {
        _workChoices.Add(new WorkChoice(null, "All work types"));
        foreach (WorkType workType in Enum.GetValues<WorkType>())
            _workChoices.Add(new WorkChoice(workType, WorkTypeText.Label(workType)));

        WorkTypeCombo.ItemsSource = _workChoices;
        WorkTypeCombo.SelectedIndex = 0;
    }

    private void BuildStatCards()
    {
        TasklistStats.Children.Add(Card("CompletedTasklists", "Completed Tasklists", "In this period", "success", CheckIcon(), AppearanceService.SuccessTextBrush));
        TasklistStats.Children.Add(Card("StartedTasklists", "Started Tasklists", "First start in this period", "accent", PlayIcon(), AppearanceService.AccentTextBrush));
        TasklistStats.Children.Add(Card("InProgressTasklists", "In Progress", "Current, active in period", "warning", RefreshIcon(), AppearanceService.WarningTextBrush));
        TasklistStats.Children.Add(Card("PendingTasklists", "Pending", "Current, active in period", "muted", ClockIcon(), AppearanceService.TextMutedBrush));
        CheckStats.Children.Add(Card("ChecksCompleted", "Checks Completed", "In this period", "success", ListIcon(), AppearanceService.SuccessTextBrush));
        CheckStats.Children.Add(Card("ChecksReopened", "Checks Reopened", "In this period", "accent", UndoIcon(), AppearanceService.AccentTextBrush));
        CheckStats.Children.Add(Card("PendingChecks", "Pending Checks", "Current, in active tasklists", "info", ListIcon(), AppearanceService.InfoTextBrush));
    }

    private Control Card(string key, string label, string hint, string tone, Geometry icon, string strokeKey)
    {
        var value = new TextBlock { Text = "0", Classes = { "stat-value" } };
        _stats[key] = value;
        var plate = new Border { Classes = { "tone", tone } };
        plate.Child = new Path
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Stroke = AppearanceService.Brush(strokeKey),
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Fill = strokeKey == AppearanceService.AccentTextBrush && tone == "accent" && key == "StartedTasklists"
                ? AppearanceService.Brush(strokeKey)
                : Brushes.Transparent,
            Data = icon,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (key == "StartedTasklists")
            ((Path)plate.Child).Fill = AppearanceService.Brush(strokeKey);

        return new Border
        {
            Classes = { "history-card" },
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    plate,
                    new StackPanel
                    {
                        Spacing = 1,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            value,
                            new TextBlock { Text = label, Classes = { "stat-label" } },
                            new TextBlock { Text = hint, Classes = { "stat-hint" } }
                        }
                    }
                }
            }
        };
    }

    private void SetStat(string key, int value)
    {
        if (_stats.TryGetValue(key, out var text))
            text.Text = value.ToString("N0", CultureInfo.CurrentCulture);
    }

    private void ApplyProjects(HistoryViewState state)
    {
        var signature = state.ProjectScope + ":" + state.ProjectId;
        foreach (var project in state.Projects)
            signature += "|" + project.ProjectId + ":" + project.Name;

        if (signature != _projectSignature)
        {
            _projectSignature = signature;
            var choices = new List<ProjectChoice>
            {
                new(HistoryProjectScope.All, null, "All Projects"),
                new(HistoryProjectScope.Standalone, null, "Standalone tasklists")
            };
            foreach (var project in state.Projects)
                choices.Add(new ProjectChoice(HistoryProjectScope.Project, project.ProjectId, project.Name));

            ProjectCombo.ItemsSource = choices;
        }

        var index = 0;
        if (ProjectCombo.ItemsSource is IList<ProjectChoice> items)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var choice = items[i];
                if (choice.Scope == state.ProjectScope && choice.ProjectId == state.ProjectId)
                {
                    index = i;
                    break;
                }
            }
        }

        if (ProjectCombo.SelectedIndex != index)
            ProjectCombo.SelectedIndex = index;
    }

    private void ApplyWorkType(WorkType? workType)
    {
        var index = 0;
        for (var i = 0; i < _workChoices.Count; i++)
        {
            if (_workChoices[i].WorkType == workType)
            {
                index = i;
                break;
            }
        }

        if (WorkTypeCombo.SelectedIndex != index)
            WorkTypeCombo.SelectedIndex = index;
    }

    private void BuildTrend(HistoryDashboard dashboard)
    {
        TrendHost.Children.Clear();
        var buckets = dashboard.Trend;
        if (buckets.Count == 0)
            return;

        var peak = 0;
        foreach (var bucket in buckets)
            peak = Math.Max(peak, bucket.Count);

        var axisMax = NiceMax(peak);
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*") };
        var axis = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(0, 0, 8, 0) };
        var labels = new Canvas { VerticalAlignment = VerticalAlignment.Stretch };
        for (var tick = 0; tick <= 4; tick++)
        {
            var value = (int)Math.Round(axisMax * (4 - tick) / 4.0, MidpointRounding.AwayFromZero);
            var label = new TextBlock
            {
                Text = value.ToString(CultureInfo.CurrentCulture),
                FontSize = 11,
                Foreground = AppearanceService.Brush(AppearanceService.TextFaintBrush),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Canvas.SetRight(label, 0);
            Canvas.SetTop(label, tick / 4.0 * 168);
            labels.Children.Add(label);
        }

        axis.Children.Add(labels);
        Grid.SetRow(labels, 0);
        var spacer = new Border { Height = 22 };
        Grid.SetRow(spacer, 1);
        axis.Children.Add(spacer);
        layout.Children.Add(axis);

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        var plot = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(),
            RowDefinitions = new RowDefinitions("*,Auto"),
            MinWidth = Math.Max(280, buckets.Count * 24)
        };
        for (var index = 0; index < buckets.Count; index++)
        {
            plot.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var bucket = buckets[index];
            var column = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            var barHost = new Border { Height = 168, VerticalAlignment = VerticalAlignment.Bottom };
            var fraction = axisMax == 0 ? 0 : (double)bucket.Count / axisMax;
            var bar = new Border
            {
                Height = Math.Max(0, fraction * 168),
                Margin = new Thickness(3, 0),
                CornerRadius = new CornerRadius(5, 5, 2, 2),
                Background = bucket.Count == 0 ? Brushes.Transparent : AppearanceService.Brush(AppearanceService.AccentBrush),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            ToolTip.SetTip(bar, bucket.Count == 1 ? bucket.Label + ": 1 check" : bucket.Label + ": " + bucket.Count + " checks");
            barHost.Child = bar;
            column.Children.Add(barHost);
            var caption = new TextBlock
            {
                Text = ShowLabel(index, buckets.Count) ? bucket.Label : string.Empty,
                FontSize = 11,
                Height = 18,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = AppearanceService.Brush(AppearanceService.TextFaintBrush),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(caption, 1);
            column.Children.Add(caption);
            Grid.SetColumn(column, index);
            plot.Children.Add(column);
        }

        scroll.Content = plot;
        Grid.SetColumn(scroll, 1);
        layout.Children.Add(scroll);
        TrendHost.Children.Add(layout);
    }

    private void BuildCompleted(HistoryDashboard dashboard, HistoryViewState state)
    {
        RecentHost.Children.Clear();
        var filtered = !string.IsNullOrWhiteSpace(state.Search) || state.WorkType is not null;
        RecentEmpty.Text = filtered
            ? "No completed tasklists match these filters."
            : "No completed tasklists in this period.";
        RecentEmpty.IsVisible = dashboard.Completed.Count == 0;
        ViewAllButton.IsVisible = dashboard.CompletedMatchCount > HistoryStatisticsService.PreviewLimit;
        ViewAllButton.Content = _showingAll ? "Show less" : "View All →";

        foreach (var entry in dashboard.Completed)
            RecentHost.Children.Add(CompletedRow(entry));
    }

    private Control CompletedRow(CompletedTasklistEntry entry)
    {
        var tint = Tint(entry.WorkType);
        var button = new Button { Classes = { "history-row" }, Tag = entry };
        button.Click += (_, _) => OpenRequested?.Invoke(this, entry);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 12
        };
        grid.Children.Add(Mark(tint.Plate, tint.Glyph, IconFor(entry.WorkType)));

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = entry.Title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = AppearanceService.Brush(AppearanceService.TextPrimaryBrush),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrWhiteSpace(entry.ProjectName))
        {
            text.Children.Add(new TextBlock
            {
                Text = entry.ProjectName,
                FontSize = 12,
                Foreground = AppearanceService.Brush(AppearanceService.TextMutedBrush),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var column = 2;
        AddPill(grid, ref column, WorkTypeText.Label(entry.WorkType), tint.Plate, tint.Glyph);
        if (!string.IsNullOrWhiteSpace(entry.Framework))
            AddPill(grid, ref column, entry.Framework, AppearanceService.CardBrush, AppearanceService.TextSecondaryBrush);

        var when = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(entry.CompletedAtUtc, DateTimeKind.Utc),
            TimeZoneInfo.Local);
        var date = new TextBlock
        {
            Text = when.ToString("MMM d, yyyy", CultureInfo.CurrentCulture),
            FontSize = 13,
            Foreground = AppearanceService.Brush(AppearanceService.TextMutedBrush),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(date, column++);
        grid.Children.Add(date);
        var open = new Path
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Stroke = AppearanceService.Brush(AppearanceService.TextFaintBrush),
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            Data = Geometry.Parse("M4,11 V4 H11 M7,8 L11.5,3.5 M8,3.5 H11.5 V7"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(open, column);
        grid.Children.Add(open);
        button.Content = grid;
        return button;
    }

    private static void AddPill(Grid grid, ref int column, string text, string plate, string glyph)
    {
        var pill = new Border
        {
            Background = AppearanceService.Brush(plate),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = AppearanceService.Brush(glyph)
            }
        };
        Grid.SetColumn(pill, column++);
        grid.Children.Add(pill);
    }

    private static Control Mark(string plate, string glyph, string data) =>
        new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(10),
            Background = AppearanceService.Brush(plate),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Path
            {
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                Stroke = AppearanceService.Brush(glyph),
                StrokeThickness = 1.6,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent,
                Data = Geometry.Parse(data),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

    private void ArrangeForWidth(double width)
    {
        if (width <= 0 || TasklistStats is null)
            return;

        var columns = width < 860 ? 2 : 4;
        TasklistStats.Columns = columns;
        CheckStats.Columns = columns;
        var stacked = width < 780;
        TrendLayout.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(260);
        Grid.SetColumn(RateCard, stacked ? 0 : 1);
        Grid.SetRow(RateCard, stacked ? 1 : 0);
        Grid.SetColumnSpan(TrendLayout.Children[0], stacked ? 2 : 1);
    }

    private static void SelectPeriod(Button button, bool selected)
    {
        if (selected)
            button.Classes.Add("selected");
        else
            button.Classes.Remove("selected");
    }

    private static void SelectSmall(Button button, bool selected) => SelectPeriod(button, selected);

    private static void SetDate(CalendarDatePicker picker, DateOnly? date)
    {
        var next = date?.ToDateTime(TimeOnly.MinValue);
        if (picker.SelectedDate != next)
            picker.SelectedDate = next;
    }

    private static DateOnly? ReadDate(CalendarDatePicker picker) =>
        picker.SelectedDate is DateTime value ? DateOnly.FromDateTime(value) : null;

    private static bool ShowLabel(int index, int count)
    {
        if (count <= 8)
            return true;

        var step = count <= 16 ? 2 : count <= 31 ? 5 : Math.Max(1, count / 8);
        return index % step == 0 || index == count - 1;
    }

    private static int NiceMax(int peak)
    {
        if (peak <= 1)
            return 1;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(peak)));
        var scaled = peak / magnitude;
        var nice = scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 5 ? 5 : 10;
        return (int)(nice * magnitude);
    }

    private static (string Plate, string Glyph) Tint(WorkType workType) =>
        workType switch
        {
            WorkType.Bug => (AppearanceService.DangerSurfaceBrush, AppearanceService.DangerTextBrush),
            WorkType.Feature => (AppearanceService.AccentSubtleBrush, AppearanceService.AccentTextBrush),
            WorkType.Development => (AppearanceService.SuccessSubtleBrush, AppearanceService.SuccessTextBrush),
            WorkType.Testing => (AppearanceService.InfoSurfaceBrush, AppearanceService.InfoTextBrush),
            WorkType.PullRequest => (AppearanceService.ReviewSurfaceBrush, AppearanceService.ReviewTextBrush),
            WorkType.Daily => (AppearanceService.WarningSurfaceBrush, AppearanceService.WarningTextBrush),
            _ => (AppearanceService.CardBrush, AppearanceService.TextSecondaryBrush)
        };

    private static string IconFor(WorkType workType) =>
        workType switch
        {
            WorkType.Bug => "M8,3.5 V6 M6.2,6.2 A3.2,3.2 0 1 0 9.8,6.2 M5,8.5 H11 M6,11.5 H10",
            WorkType.Feature => "M8,2.2 L9.4,6 H13.4 L10.2,8.3 L11.5,12.2 L8,9.8 L4.5,12.2 L5.8,8.3 L2.6,6 H6.6 Z",
            WorkType.Development => "M5.5,5.5 L2.5,8 L5.5,10.5 M10.5,5.5 L13.5,8 L10.5,10.5 M9,4.5 L7,11.5",
            _ => "M3,2 H9 L12,5 V14 H3 Z M9,2 V5 H12"
        };

    private static Geometry CheckIcon() => Geometry.Parse("M8,1.6 A6.4,6.4 0 1 0 8,14.4 A6.4,6.4 0 1 0 8,1.6 M5.1,8.1 L7.1,10.1 L11.1,5.6");

    private static Geometry PlayIcon() => Geometry.Parse("M6.2,4.2 L12,8 L6.2,11.8 Z");

    private static Geometry RefreshIcon() => Geometry.Parse("M12.6,6.2 A5,5 0 1 0 13,9.2 M12.6,3.4 V6.4 H9.6");

    private static Geometry ClockIcon() => Geometry.Parse("M8,1.8 A6.2,6.2 0 1 0 8,14.2 A6.2,6.2 0 1 0 8,1.8 M8,4.6 V8.2 L10.5,9.6");

    private static Geometry ListIcon() => Geometry.Parse("M6.2,3.6 H13 M6.2,8 H13 M6.2,12.4 H13 M3.2,3.6 H4 M3.2,8 H4 M3.2,12.4 H4");

    private static Geometry UndoIcon() => Geometry.Parse("M6.2,6.2 H11.4 A2.8,2.8 0 1 1 11.4,11 H7 M6.2,6.2 L8.4,4 M6.2,6.2 L8.4,8.4");

    private void Back_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        BackRequested?.Invoke(this, EventArgs.Empty);

    private void Week_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WeekRequested?.Invoke(this, EventArgs.Empty);

    private void Month_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        MonthRequested?.Invoke(this, EventArgs.Empty);

    private void Custom_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        CustomPeriodRequested?.Invoke(this, EventArgs.Empty);

    private void Daily_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_applying || DailyButton.Classes.Contains("selected"))
            return;

        _requestedGranularity = TrendGranularity.Daily;
        GranularityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Weekly_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_applying || WeeklyButton.Classes.Contains("selected"))
            return;

        _requestedGranularity = TrendGranularity.Weekly;
        GranularityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CustomDate_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_applying)
            return;

        CustomDatesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ProjectCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_applying)
            return;

        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    private void WorkTypeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_applying)
            return;

        WorkTypeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_applying)
            return;

        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ViewAll_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ViewAllRequested?.Invoke(this, EventArgs.Empty);

    private sealed record ProjectChoice(HistoryProjectScope Scope, Guid? ProjectId, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record WorkChoice(WorkType? WorkType, string Name)
    {
        public override string ToString() => Name;
    }
}

public sealed class HistoryViewState
{
    public HistoryPeriodKind Period { get; init; }

    public DateOnly? CustomStart { get; init; }

    public DateOnly? CustomEnd { get; init; }

    public string? RangeError { get; init; }

    public HistoryProjectScope ProjectScope { get; init; }

    public Guid? ProjectId { get; init; }

    public IReadOnlyList<HistoryProjectOption> Projects { get; init; } = [];

    public string Search { get; init; } = string.Empty;

    public WorkType? WorkType { get; init; }

    public bool ShowingAllCompleted { get; init; }
}
