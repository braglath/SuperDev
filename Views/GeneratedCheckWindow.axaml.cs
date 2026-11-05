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
    private readonly List<ReferenceEditor> _references = [];

    public GeneratedCheckWindow(GeneratedCheck check, string? notice, bool noticeIsAdjustment = false)
    {
        ArgumentNullException.ThrowIfNull(check);
        InitializeComponent();

        TitleBox.Text = check.Title;
        NotesBox.Text = check.Description?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(NotesBox.Text))
            UseNotesBox.IsChecked = false;

        AcceptanceBox.Text = check.AcceptanceCriteria?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(AcceptanceBox.Text))
            UseAcceptanceBox.IsChecked = false;

        TypeBox.ItemsSource = ChecklistVocabulary.Types;
        var itemType = ChecklistVocabulary.CanonicalItemType(check.Type);
        TypeBox.SelectedItem = itemType ?? ChecklistVocabulary.DefaultType;
        if (itemType is null)
            UseTypeBox.IsChecked = false;

        AddReference("Test reports", check.TestReports, "Report, path, or link");
        AddReference("Logs", check.Logs, "Log excerpt or link");
        AddReference("Code references", check.CodeReferences, "File, symbol, or link");
        AddReference("API responses", check.ApiResponses, "Response excerpt or link");
        AddReference("PR links", check.PullRequestLinks, "Pull request link");

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
            UseAcceptance = UseAcceptanceBox.IsChecked == true,
            Acceptance = AcceptanceBox.Text?.Trim() ?? string.Empty,
            UseType = UseTypeBox.IsChecked == true,
            ItemType = TypeBox.SelectedItem as string ?? string.Empty,
            UseTestReports = UseReference(0),
            TestReports = ReferenceText(0),
            UseLogs = UseReference(1),
            Logs = ReferenceText(1),
            UseCodeReferences = UseReference(2),
            CodeReferences = ReferenceText(2),
            UseApiResponses = UseReference(3),
            ApiResponses = ReferenceText(3),
            UsePullRequestLinks = UseReference(4),
            PullRequestLinks = ReferenceText(4),
            Subtasks = subtasks
        });
    }

    private bool UseReference(int index) =>
        index < _references.Count && _references[index].Use.IsChecked == true;

    private string ReferenceText(int index) =>
        index < _references.Count ? _references[index].Text.Text?.Trim() ?? string.Empty : string.Empty;

    private void AddReference(string label, string? text, string placeholder)
    {
        var filled = !string.IsNullOrWhiteSpace(text);
        var use = new CheckBox
        {
            Classes = { "choose" },
            Content = label,
            IsChecked = filled
        };
        use.IsCheckedChanged += Selection_Changed;
        var box = new TextBox
        {
            Classes = { "notes" },
            MinHeight = 72,
            Text = text?.Trim() ?? string.Empty,
            PlaceholderText = placeholder,
            IsEnabled = filled
        };
        ReferenceEditors.Children.Add(new StackPanel
        {
            Spacing = 6,
            Children = { use, box }
        });
        _references.Add(new ReferenceEditor(use, box));
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
        AcceptanceBox.IsEnabled = UseAcceptanceBox.IsChecked == true;
        TypeBox.IsEnabled = UseTypeBox.IsChecked == true;
        var any = TitleBox.IsEnabled || CategoryBox.IsEnabled || PriorityBox.IsEnabled
            || NotesBox.IsEnabled || AcceptanceBox.IsEnabled || TypeBox.IsEnabled;
        foreach (var reference in _references)
        {
            var chosen = reference.Use.IsChecked == true;
            reference.Text.IsEnabled = chosen;
            if (chosen)
                any = true;
        }
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

    private sealed class ReferenceEditor(CheckBox use, TextBox text)
    {
        public CheckBox Use { get; } = use;

        public TextBox Text { get; } = text;
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

    public bool UseAcceptance { get; init; }

    public string Acceptance { get; init; } = string.Empty;

    public bool UseType { get; init; }

    public string ItemType { get; init; } = string.Empty;

    public bool UseTestReports { get; init; }

    public string TestReports { get; init; } = string.Empty;

    public bool UseLogs { get; init; }

    public string Logs { get; init; } = string.Empty;

    public bool UseCodeReferences { get; init; }

    public string CodeReferences { get; init; } = string.Empty;

    public bool UseApiResponses { get; init; }

    public string ApiResponses { get; init; } = string.Empty;

    public bool UsePullRequestLinks { get; init; }

    public string PullRequestLinks { get; init; } = string.Empty;

    public IReadOnlyList<string> Subtasks { get; init; } = [];
}
