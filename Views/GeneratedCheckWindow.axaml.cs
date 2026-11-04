using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using SuperDev.Models;
using SuperDev.Services;
using SuperDev.Services.Ai;

namespace SuperDev.Views;

public partial class GeneratedCheckWindow : Window
{
    private readonly List<SubtaskRow> _subtasks = [];

    public GeneratedCheckWindow(GeneratedCheck check, string? notice, bool noticeIsAdjustment = false)
    {
        ArgumentNullException.ThrowIfNull(check);
        InitializeComponent();

        TitleBox.Text = check.Title;
        NotesBox.Text = AiChecklistMapper.ToNotes(new GeneratedCheckItem
        {
            Description = check.Description,
            AcceptanceCriteria = check.AcceptanceCriteria
        });
        if (string.IsNullOrWhiteSpace(NotesBox.Text))
            UseNotesBox.IsChecked = false;

        CategoryBox.ItemsSource = CheckCategories.All;
        CategoryBox.SelectedItem = Array.IndexOf(CheckCategories.All, check.Category) >= 0
            ? check.Category
            : CheckCategories.Default;

        PriorityBox.ItemsSource = ChecklistVocabulary.Priorities;
        var priority = AiChecklistMapper.ToPriority(new GeneratedCheckItem { Priority = check.Priority });
        PriorityBox.SelectedIndex = (int)priority;
        ApplyPriorityColor();

        NoticeText.IsVisible = !string.IsNullOrWhiteSpace(notice);
        NoticeText.Text = notice ?? string.Empty;
        if (noticeIsAdjustment)
            NoticeText.Foreground = AppearanceService.Brush("WarningTextBrush");

        foreach (var subtask in check.Subtasks)
            AddSubtask(subtask);
        EmptySubtasks.IsVisible = _subtasks.Count == 0;
        ApplySelection();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape)
            return;

        e.Handled = true;
        Close(null);
    }

    private void Selection_Changed(object? sender, RoutedEventArgs e) =>
        ApplySelection();

    private void PriorityBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ApplyPriorityColor();

    private void Omit_Click(object? sender, RoutedEventArgs e) =>
        Close(null);

    private void Use_Click(object? sender, RoutedEventArgs e)
    {
        var subtasks = new List<string>();
        foreach (var row in _subtasks)
        {
            if (row.Use.IsChecked != true)
                continue;

            var title = row.Title.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(title))
                subtasks.Add(title);
        }

        var priorityIndex = PriorityBox.SelectedIndex;
        if (priorityIndex < 0 || priorityIndex > (int)ChecklistPriority.Review)
            priorityIndex = (int)ChecklistPriority.Required;

        Close(new GeneratedCheckChoice
        {
            UseTitle = UseTitleBox.IsChecked == true,
            Title = TitleBox.Text?.Trim() ?? string.Empty,
            UseCategory = UseCategoryBox.IsChecked == true,
            Category = CategoryBox.SelectedItem as string ?? CheckCategories.Default,
            UsePriority = UsePriorityBox.IsChecked == true,
            Priority = (ChecklistPriority)priorityIndex,
            UseNotes = UseNotesBox.IsChecked == true,
            Notes = NotesBox.Text?.Trim() ?? string.Empty,
            Subtasks = subtasks
        });
    }

    private void AddSubtask(string title)
    {
        var use = new CheckBox
        {
            Classes = { "choose" },
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        use.IsCheckedChanged += Selection_Changed;
        ToolTip.SetTip(use, "Include this subtask");
        var text = new TextBox
        {
            Classes = { "field" },
            Text = title
        };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(use);
        row.Children.Add(text);
        _subtasks.Add(new SubtaskRow(use, text));
        SubtaskList.Children.Add(row);
    }

    private void ApplySelection()
    {
        if (TitleBox is null || UseButton is null)
            return;

        TitleBox.IsEnabled = UseTitleBox.IsChecked == true;
        CategoryBox.IsEnabled = UseCategoryBox.IsChecked == true;
        PriorityBox.IsEnabled = UsePriorityBox.IsChecked == true;
        NotesBox.IsEnabled = UseNotesBox.IsChecked == true;
        var any = TitleBox.IsEnabled || CategoryBox.IsEnabled || PriorityBox.IsEnabled || NotesBox.IsEnabled;
        foreach (var row in _subtasks)
        {
            var chosen = row.Use.IsChecked == true;
            row.Title.IsEnabled = chosen;
            if (chosen)
                any = true;
        }

        UseButton.IsEnabled = any;
    }

    private void ApplyPriorityColor()
    {
        if (PriorityBox.SelectedIndex < 0)
            return;

        PriorityBox.Foreground = PriorityAppearance.BrushFor((ChecklistPriority)PriorityBox.SelectedIndex);
    }

    private sealed class SubtaskRow(CheckBox use, TextBox title)
    {
        public CheckBox Use { get; } = use;

        public TextBox Title { get; } = title;
    }
}

public sealed class GeneratedCheckChoice
{
    public bool UseTitle { get; init; }

    public string Title { get; init; } = string.Empty;

    public bool UseCategory { get; init; }

    public string Category { get; init; } = string.Empty;

    public bool UsePriority { get; init; }

    public ChecklistPriority Priority { get; init; }

    public bool UseNotes { get; init; }

    public string Notes { get; init; } = string.Empty;

    public IReadOnlyList<string> Subtasks { get; init; } = [];
}
