using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SuperDev.Models;

namespace SuperDev.Views;

public partial class CheckPicker : UserControl
{
    private sealed class Row
    {
        public required CheckBox Box { get; init; }

        public required ChecklistItem Item { get; init; }

        public required int Index { get; init; }

        public bool Selected { get; set; }
    }

    private readonly List<Row> _rows = [];
    private readonly HashSet<Row> _shown = [];
    private bool _bulk;
    private bool _narrowed;

    public CheckPicker()
    {
        InitializeComponent();
        StatusFilterBox.SelectedIndex = 0;
        PriorityFilterBox.SelectedIndex = 0;
        SortBox.SelectedIndex = 0;
        ApplyPriorityFilterColor();

        SearchBox.TextChanged += (_, _) => ApplyFilter();
        StatusFilterBox.SelectionChanged += Filter_Changed;
        PriorityFilterBox.SelectionChanged += Filter_Changed;
        SortBox.SelectionChanged += Filter_Changed;
    }

    public event EventHandler? SelectionChanged;

    public int SelectedCount => _rows.Count(row => row.Selected);

    public IReadOnlyList<int> SelectedIndexes =>
        _rows
            .Where(row => row.Selected)
            .Select(row => row.Index)
            .ToList();

    public void SetChecks(IReadOnlyList<ChecklistItem> checks)
    {
        _rows.Clear();
        _shown.Clear();
        ChecksPanel.Children.Clear();

        for (var index = 0; index < checks.Count; index++)
        {
            var item = checks[index];
            var box = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = Label(item),
                    Classes = { "check-label" },
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                },
                VerticalAlignment = VerticalAlignment.Center
            };
            var row = new Row { Box = box, Item = item, Index = index };
            box.IsCheckedChanged += (_, _) => OnChecked(row);
            _rows.Add(row);
        }

        ApplyFilter();
    }

    private void Filter_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ApplyPriorityFilterColor();
        ApplyFilter();
    }

    private void ApplyPriorityFilterColor()
    {
        PriorityFilterBox.Foreground = PriorityFilterBox.SelectedIndex switch
        {
            1 => PriorityAppearance.Critical,
            2 => PriorityAppearance.Required,
            3 => PriorityAppearance.Conditional,
            4 => PriorityAppearance.Review,
            _ => PriorityAppearance.Neutral
        };
    }

    private void ApplyFilter()
    {
        var search = SearchBox.Text?.Trim();
        var status = Math.Max(StatusFilterBox.SelectedIndex, 0);
        var priority = Math.Max(PriorityFilterBox.SelectedIndex, 0);
        var sort = Math.Max(SortBox.SelectedIndex, 0);
        _narrowed = !string.IsNullOrEmpty(search) || status > 0 || priority > 0;

        var shown = _rows.Where(row => Matches(row.Item, search, status, priority));
        shown = sort switch
        {
            1 => shown.OrderBy(row => row.Item.Priority).ThenBy(row => row.Index),
            2 => shown
                .OrderBy(row => row.Item.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Index),
            3 => shown
                .OrderBy(row => row.Item.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Index),
            _ => shown.OrderBy(row => row.Index)
        };

        var visible = shown.ToList();
        _shown.Clear();
        _bulk = true;
        ChecksPanel.Children.Clear();
        foreach (var row in visible)
        {
            row.Box.IsVisible = true;
            row.Box.IsChecked = row.Selected;
            _shown.Add(row);
            ChecksPanel.Children.Add(row.Box);
        }

        foreach (var row in _rows)
        {
            if (!_shown.Contains(row))
                row.Box.IsVisible = false;
        }

        _bulk = false;

        var total = _rows.Count;
        EmptyText.IsVisible = total == 0;
        NoMatchesText.IsVisible = total > 0 && visible.Count == 0;
        SelectAllButton.Content = _narrowed ? "Select shown" : "Select all";
        SelectAllButton.IsEnabled = visible.Count > 0;
        ResetButton.IsEnabled = _narrowed || sort > 0;
        PublishSelection();
    }

    private void OnChecked(Row row)
    {
        if (_bulk)
            return;

        row.Selected = row.Box.IsChecked == true;
        PublishSelection();
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e)
    {
        _bulk = true;
        foreach (var row in _shown)
        {
            row.Selected = true;
            row.Box.IsChecked = true;
        }

        _bulk = false;
        PublishSelection();
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        _bulk = true;
        foreach (var row in _rows)
        {
            row.Selected = false;
            row.Box.IsChecked = false;
        }

        _bulk = false;
        PublishSelection();
    }

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        StatusFilterBox.SelectedIndex = 0;
        PriorityFilterBox.SelectedIndex = 0;
        SortBox.SelectedIndex = 0;
    }

    private void PublishSelection()
    {
        var selected = SelectedCount;
        var notShown = _rows.Count(row => row.Selected && !_shown.Contains(row));
        var text = selected == 1 ? "1 selected" : $"{selected} selected";
        if (_narrowed)
            text = $"{_shown.Count} of {_rows.Count} · {text}";
        if (notShown > 0)
            text = notShown == 1 ? $"{text} · 1 not shown" : $"{text} · {notShown} not shown";

        SelectionCount.Text = text;
        ToolTip.SetTip(SelectionCount, text);
        ClearButton.IsEnabled = selected > 0;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Label(ChecklistItem item) =>
        string.IsNullOrWhiteSpace(item.Detail)
            ? item.Title
            : $"{item.Title} — {item.Detail}";

    private static bool Matches(ChecklistItem item, string? search, int status, int priority)
    {
        var statusMatches = status switch
        {
            1 => !item.IsCompleted && !item.IsSkipped,
            2 => item.IsCompleted,
            3 => item.IsSkipped,
            _ => true
        };
        if (!statusMatches)
            return false;

        var priorityMatches = priority switch
        {
            1 => item.Priority == ChecklistPriority.Critical,
            2 => item.Priority == ChecklistPriority.Required,
            3 => item.Priority == ChecklistPriority.Conditional,
            4 => item.Priority == ChecklistPriority.Review,
            _ => true
        };
        if (!priorityMatches)
            return false;

        return MatchesSearch(item, search);
    }

    private static bool MatchesSearch(ChecklistItem item, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        return item.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               item.Category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               item.Notes.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               (item.Acceptance?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (item.ItemType?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
               item.MatchesReference(search) ||
               (item.Evidence ?? []).Any(file =>
                   file is not null &&
                   (EvidenceKind.DisplayName(file.Path).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    file.Path.Contains(search, StringComparison.OrdinalIgnoreCase))) ||
               (item.Subtasks ?? []).Any(subtask =>
                   subtask.Title.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
               (item.Deadline is { } deadline &&
                DeadlineValue.Format(deadline, item.DeadlineMinute)
                    .Contains(search, StringComparison.OrdinalIgnoreCase));
    }
}
