using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
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
    private Border? _dragRow;
    private IPointer? _pointer;
    private Avalonia.Point _dragStart;
    private bool _dragging;
    private int _pendingTarget = -1;
    private bool _moveQueued;

    public EditTemplateWindow(ChecklistTemplate template, ChecklistTemplateService templates, bool creating = false)
    {
        InitializeComponent();
        AddHandler(PointerMovedEvent, DragPointerMoved, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, DragPointerReleased, RoutingStrategies.Tunnel, true);
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
        var grip = new Border
        {
            Classes = { "check-grip" },
            Child = new TextBlock
            {
                Text = "⋮⋮",
                Classes = { "check-grip-mark" }
            }
        };
        ToolTip.SetTip(grip, "Drag to reorder");

        var fields = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("22,*,140,130,32"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        Grid.SetColumn(category, 2);
        Grid.SetColumn(priority, 3);
        Grid.SetColumn(remove, 4);
        fields.Children.Add(grip);
        fields.Children.Add(title);
        fields.Children.Add(category);
        fields.Children.Add(priority);
        fields.Children.Add(remove);

        var row = new Border
        {
            Classes = { "check-row" },
            Child = fields
        };

        var editor = new CheckEditor(row, title, category, priority);
        remove.Click += (_, _) => RemoveCheck(editor);
        grip.PointerPressed += (_, e) => StartDrag(row, e);

        _checks.Add(editor);
        ChecksList.Children.Add(row);
        if (focus)
            title.Focus();
    }

    private void RemoveCheck(CheckEditor editor)
    {
        if (ReferenceEquals(_dragRow, editor.Row))
        {
            var pointer = _pointer;
            _pendingTarget = -1;
            ClearDrag();
            if (pointer?.Captured == this)
                pointer.Capture(null);
        }

        _checks.Remove(editor);
        ChecksList.Children.Remove(editor.Row);
    }

    private void StartDrag(Border row, PointerPressedEventArgs e)
    {
        if (_dragRow is not null)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // The row is removed from the list as it moves. Capturing it would end the gesture after one step.
        _dragRow = row;
        _pointer = e.Pointer;
        _dragStart = e.GetPosition(ChecksList);
        _dragging = false;
        _pendingTarget = -1;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void DragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragRow is not { } row || _pointer != e.Pointer)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndDrag(e);
            return;
        }

        var position = e.GetPosition(ChecksList);
        if (!_dragging)
        {
            var delta = position - _dragStart;
            if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4)
                return;

            _dragging = true;
            row.Classes.Add("dragging");
            row.ZIndex = 2;
        }

        ScrollWhileDragging(e.GetPosition(ChecksScroll));
        QueueMove(IndexAt(position.Y, row));
        e.Handled = true;
    }

    private void DragPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragRow is null || _pointer != e.Pointer)
            return;

        EndDrag(e);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragRow is null)
            return;

        CommitPendingMove();
        ClearDrag();
    }

    private void EndDrag(PointerEventArgs e)
    {
        CommitPendingMove();
        ClearDrag();
        if (e.Pointer.Captured == this)
            e.Pointer.Capture(null);
    }

    private void ClearDrag()
    {
        if (_dragRow is { } row)
        {
            row.Classes.Remove("dragging");
            row.ZIndex = 0;
        }

        _dragRow = null;
        _pointer = null;
        _dragging = false;
        _pendingTarget = -1;
    }

    private void QueueMove(int target)
    {
        _pendingTarget = target;
        if (_moveQueued)
            return;

        _moveQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _moveQueued = false;
            CommitPendingMove();
        }, DispatcherPriority.Input);
    }

    private void CommitPendingMove()
    {
        if (_dragRow is not Border row)
            return;

        var target = _pendingTarget;
        _pendingTarget = -1;
        if (target >= 0)
            MoveCheck(row, target);
    }

    private void ScrollWhileDragging(Avalonia.Point pointer)
    {
        var height = ChecksScroll.Bounds.Height;
        if (height <= 0)
            return;

        var offset = ChecksScroll.Offset.Y;
        if (pointer.Y < 28)
            offset -= 16;
        else if (pointer.Y > height - 28)
            offset += 16;
        else
            return;

        var max = ChecksScroll.Extent.Height - height;
        if (max < 0)
            max = 0;
        if (offset < 0)
            offset = 0;
        if (offset > max)
            offset = max;

        ChecksScroll.Offset = new Avalonia.Vector(0, offset);
    }

    private int IndexAt(double y, Border dragging)
    {
        var index = 0;
        foreach (var check in _checks)
        {
            if (ReferenceEquals(check.Row, dragging))
                continue;

            var bounds = check.Row.Bounds;
            if (y > bounds.Y + bounds.Height / 2)
                index++;
        }

        return index;
    }

    private void MoveCheck(Border row, int target)
    {
        var from = _checks.FindIndex(check => ReferenceEquals(check.Row, row));
        if (from < 0 || target < 0 || target == from || target >= _checks.Count)
            return;

        var editor = _checks[from];
        _checks.RemoveAt(from);
        _checks.Insert(target, editor);
        ChecksList.Children.RemoveAt(from);
        ChecksList.Children.Insert(target, row);
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

        public CheckEditor(Border row, TextBox title, ComboBox category, ComboBox priority)
        {
            Row = row;
            _title = title;
            _category = category;
            _priority = priority;
        }

        public Border Row { get; }

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
