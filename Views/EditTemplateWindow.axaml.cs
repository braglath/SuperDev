using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using SuperDev.Models;
using SuperDev.Services;

namespace SuperDev.Views;

public enum TemplateEditResult
{
    Cancel = 0,
    Saved = 1,
    Created = 2,
    Deleted = 3
}

public partial class EditTemplateWindow : Window
{
    private static readonly string[] Categories =
    [
        "General",
        "Understand",
        "Development",
        "Testing",
        "Communication",
        "PR",
        "Merge",
        "Deployment",
        "Closure"
    ];

    private static readonly string[] Icons =
    [
        "🐛", "✨", "💻", "🧪", "🔀", "📅", "🔧", "🚀", "📌", "✅",
        "🔥", "⚡", "🛠", "📦", "🔒", "🌟", "📝", "🎯", "🧠", "🔍",
        "🐞", "🧩", "📣", "🏁", "♻️", "⚠️", "💡", "📎", "📋", "🛡", "🌱"
    ];

    private readonly ChecklistTemplate _template;
    private readonly ChecklistTemplateService _templates;
    private readonly List<Button> _iconButtons = [];
    private readonly List<CheckEditor> _checks = [];
    private readonly bool _creating;
    private string _icon;

    public EditTemplateWindow(ChecklistTemplate template, ChecklistTemplateService templates, bool creating = false)
    {
        InitializeComponent();
        _creating = creating;
        _template = template.Copy();
        _templates = templates;
        _icon = string.IsNullOrWhiteSpace(_template.Icon) ? "📋" : _template.Icon;
        TitleBox.Text = _template.Title;
        if (creating)
        {
            Title = string.IsNullOrWhiteSpace(_template.Title) ? "New template" : "Save as template";
            DeleteButton.IsVisible = false;
            SaveAsNewButton.IsVisible = false;
            SaveButton.Content = string.IsNullOrWhiteSpace(_template.Title) ? "Create" : "Save";
        }

        BuildIcons();
        foreach (var item in _template.Items)
            AddCheck(item);

        if (_checks.Count == 0)
            AddCheck(new TemplateCheck { Category = "General", Priority = ChecklistPriority.Required });
    }

    public string? ResultId { get; private set; }

    private void BuildIcons()
    {
        var options = Icons.Contains(_icon) ? Icons : new[] { _icon }.Concat(Icons).ToArray();
        foreach (var icon in options)
        {
            var button = new Button
            {
                Classes = { "icon-choice" },
                Content = icon,
                Tag = icon
            };
            if (icon == _icon)
                button.Classes.Add("on");

            button.Click += Icon_Click;
            _iconButtons.Add(button);
            IconList.Children.Add(button);
        }
    }

    private void Icon_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string icon)
            return;

        _icon = icon;
        foreach (var choice in _iconButtons)
        {
            if (ReferenceEquals(choice, button))
                choice.Classes.Add("on");
            else
                choice.Classes.Remove("on");
        }
    }

    private void AddCheck_Click(object? sender, RoutedEventArgs e) =>
        AddCheck(new TemplateCheck { Category = "General", Priority = ChecklistPriority.Required }, focus: true);

    private void AddCheck(TemplateCheck item, bool focus = false)
    {
        var title = new TextBox
        {
            Classes = { "check-field" },
            Text = item.Title,
            PlaceholderText = "Check"
        };
        var category = new ComboBox
        {
            Classes = { "field" },
            Width = 140
        };
        foreach (var name in Categories)
            category.Items.Add(name);
        var currentCategory = string.IsNullOrWhiteSpace(item.Category) ? "General" : item.Category.Trim();
        if (!Categories.Contains(currentCategory))
            category.Items.Add(currentCategory);
        category.SelectedItem = currentCategory;
        var priority = new ComboBox { Classes = { "field" }, Width = 130 };
        foreach (var value in Enum.GetValues<ChecklistPriority>())
            priority.Items.Add(value.ToString());
        priority.SelectedIndex = (int)item.Priority;

        var remove = new Button
        {
            Classes = { "remove-check" },
            Content = "×"
        };

        var row = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,140,130,32"),
            ColumnSpacing = 8
        };
        Grid.SetColumn(category, 1);
        Grid.SetColumn(priority, 2);
        Grid.SetColumn(remove, 3);
        row.Children.Add(title);
        row.Children.Add(category);
        row.Children.Add(priority);
        row.Children.Add(remove);

        var editor = new CheckEditor(row, title, category, priority);
        remove.Click += (_, _) =>
        {
            _checks.Remove(editor);
            ChecksList.Children.Remove(row);
        };

        _checks.Add(editor);
        ChecksList.Children.Add(row);
        if (focus)
            title.Focus();
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var edited = await TryReadAsync();
        if (edited is null)
            return;

        if (_creating)
        {
            var created = _templates.AddCopy(edited);
            ResultId = created.Id;
            Close(TemplateEditResult.Created);
            return;
        }

        var confirm = new StyledDialog(
            "Replace this template?",
            $"Save will overwrite \"{_template.Title}\". Checklists you already created stay as they are.",
            "Replace",
            "Cancel");
        if (await confirm.ShowDialog<bool>(this) != true)
            return;

        edited.Id = _template.Id;
        edited.WorkType = _template.WorkType;
        _templates.Replace(edited);
        ResultId = edited.Id;
        Close(TemplateEditResult.Saved);
    }

    private async void SaveAsNew_Click(object? sender, RoutedEventArgs e)
    {
        var edited = await TryReadAsync();
        if (edited is null)
            return;

        var created = _templates.AddCopy(edited);
        ResultId = created.Id;
        Close(TemplateEditResult.Created);
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        var title = string.IsNullOrWhiteSpace(TitleBox.Text) ? _template.Title : TitleBox.Text.Trim();
        var confirm = new StyledDialog(
            "Delete this template?",
            $"\"{title}\" will be removed from Home. Checklists you already created are not deleted.",
            "Delete",
            "Cancel",
            danger: true);
        if (await confirm.ShowDialog<bool>(this) != true)
            return;

        _templates.Delete(_template.Id);
        ResultId = _template.Id;
        Close(TemplateEditResult.Deleted);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) =>
        Close(TemplateEditResult.Cancel);

    private async Task<ChecklistTemplate?> TryReadAsync()
    {
        var title = TitleBox.Text?.Trim() ?? string.Empty;
        if (title.Length == 0)
        {
            var message = new StyledDialog("Title required", "Enter a name for this template.");
            await message.ShowDialog(this);
            TitleBox.Focus();
            return null;
        }

        var template = _template.Copy();
        template.Title = title;
        template.Icon = _icon;
        template.Items = _checks
            .Select(check => check.Read())
            .Where(item => !string.IsNullOrWhiteSpace(item.Title))
            .ToList();
        return template;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(TemplateEditResult.Cancel);
            return;
        }

        base.OnKeyDown(e);
    }

    private sealed class CheckEditor
    {
        private readonly TextBox _title;
        private readonly ComboBox _category;
        private readonly ComboBox _priority;

        public CheckEditor(Grid row, TextBox title, ComboBox category, ComboBox priority)
        {
            Row = row;
            _title = title;
            _category = category;
            _priority = priority;
        }

        public Grid Row { get; }

        public TemplateCheck Read()
        {
            var index = _priority.SelectedIndex;
            var priority = index >= 0 && index < Enum.GetValues<ChecklistPriority>().Length
                ? (ChecklistPriority)index
                : ChecklistPriority.Required;

            return new TemplateCheck
            {
                Title = _title.Text?.Trim() ?? string.Empty,
                Category = _category.SelectedItem as string ?? "General",
                Priority = priority
            };
        }
    }
}
