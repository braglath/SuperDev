using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SuperDev.Models;
using SuperDev.Services;

namespace SuperDev.Views;

public readonly record struct ProjectTasklistRow(
    Guid DocumentId,
    string Title,
    string Detail,
    double Percent,
    bool Missing,
    bool Open);

public sealed class ProjectDetailView : UserControl
{
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _root;
    private readonly StackPanel _tasklists;
    private Project? _project;
    private Border? _dragRow;
    private IPointer? _dragPointer;
    private Point _dragStart;
    private bool _dragging;
    private bool _reordered;
    private bool _moveQueued;
    private int _pendingTarget = -1;

    public ProjectDetailView()
    {
        _root = new StackPanel
        {
            Spacing = 18,
            Margin = new Thickness(32, 28, 32, 40),
            MaxWidth = 880,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _tasklists = new StackPanel { Spacing = 10 };
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _root
        };
        Content = _scroll;
        PointerMoved += OnTasklistPointerMoved;
        PointerReleased += OnTasklistPointerReleased;
    }

    public event EventHandler? EditRequested;

    public event EventHandler? ArchiveRequested;

    public event EventHandler? DeleteRequested;

    public event EventHandler? AddRequested;

    public event EventHandler<Guid>? OpenRequested;

    public event EventHandler<Guid>? MoveRequested;

    public event EventHandler<Guid>? RemoveRequested;

    public event EventHandler? ScheduleChanged;

    public event EventHandler<IReadOnlyList<Guid>>? Reordered;

    public void Render(Project project, ProjectProgressReport report, IReadOnlyList<ProjectTasklistRow> rows)
    {
        if (_dragRow is not null)
            ClearTasklistDrag(null);
        _project = project;
        _root.Children.Clear();
        _tasklists.Children.Clear();

        var archived = project.Status == ProjectStatus.Archived;
        var color = ProjectChrome.Brush(project.Color);
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 14
        };
        header.Children.Add(Mark(color, project.Icon, 44));
        var heading = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock
        {
            Text = project.Name,
            FontSize = AppearanceService.Font(28),
            FontWeight = FontWeight.Bold,
            Foreground = AppearanceService.Brush(AppearanceService.TextPrimaryBrush),
            TextWrapping = TextWrapping.Wrap
        });
        if (archived)
        {
            heading.Children.Add(new TextBlock
            {
                Text = "Archived",
                FontSize = AppearanceService.Font(12),
                FontWeight = FontWeight.SemiBold,
                Foreground = AppearanceService.Brush(AppearanceService.TextMutedBrush)
            });
        }

        if (!string.IsNullOrWhiteSpace(project.Description))
        {
            heading.Children.Add(new TextBlock
            {
                Text = project.Description,
                FontSize = AppearanceService.Font(14),
                Foreground = AppearanceService.Brush(AppearanceService.TextBodyBrush),
                TextWrapping = TextWrapping.Wrap
            });
        }

        var meta = Meta(project);
        if (meta.Length > 0)
        {
            heading.Children.Add(new TextBlock
            {
                Text = meta,
                FontSize = AppearanceService.Font(13),
                Foreground = AppearanceService.Brush(AppearanceService.TextMutedBrush),
                TextWrapping = TextWrapping.Wrap
            });
        }

        heading.Children.Add(new ProjectScheduleBar(project, () => ScheduleChanged?.Invoke(this, EventArgs.Empty)));

        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);
        _root.Children.Add(header);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Action("Edit", primary: false, () => EditRequested?.Invoke(this, EventArgs.Empty)));
        actions.Children.Add(Action(
            archived ? "Restore" : "Archive",
            primary: false,
            () => ArchiveRequested?.Invoke(this, EventArgs.Empty)));
        actions.Children.Add(Action("Delete", primary: false, () => DeleteRequested?.Invoke(this, EventArgs.Empty), danger: true));
        actions.Children.Add(Action("Add tasklist", primary: true, () => AddRequested?.Invoke(this, EventArgs.Empty)));
        _root.Children.Add(actions);

        var stats = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"),
            ColumnSpacing = 8
        };
        AddStat(stats, 0, "Tasklists", report.TasklistCount.ToString());
        AddStat(stats, 1, "In progress", report.InProgress.ToString());
        AddStat(stats, 2, "Completed", report.Completed.ToString());
        AddStat(stats, 3, "Not started", report.NotStarted.ToString());
        AddStat(stats, 4, "Completion", report.PercentLabel);
        _root.Children.Add(stats);
        _root.Children.Add(Bar(report.Percent, color));

        var listHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        listHeader.Children.Add(new TextBlock
        {
            Text = "Tasklists",
            FontSize = AppearanceService.Font(16),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AppearanceService.Brush(AppearanceService.TextPrimaryBrush)
        });
        _root.Children.Add(listHeader);

        if (rows.Count == 0)
        {
            _root.Children.Add(new TextBlock
            {
                Text = "No tasklists in this project yet. Add one you already have.",
                FontSize = AppearanceService.Font(14),
                Foreground = AppearanceService.Brush(AppearanceService.TextMutedBrush),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        var canReorder = rows.Count > 1;
        foreach (var row in rows)
            _tasklists.Children.Add(TasklistRow(row, color, canReorder));
        _root.Children.Add(_tasklists);
    }

    private static string Meta(Project project)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(project.Platform))
            parts.Add(project.Platform);
        if (!string.IsNullOrWhiteSpace(project.Framework))
            parts.Add(project.Framework);
        return string.Join(" · ", parts);
    }

    private static Control Mark(IBrush color, string icon, double size)
    {
        var plate = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(12),
            Background = color,
            Child = ProjectChrome.Icon(icon, Brushes.White, size * 0.42)
        };
        return plate;
    }

    private static void AddStat(Grid grid, int column, string label, string value)
    {
        var stat = Stat(label, value);
        Grid.SetColumn(stat, column);
        grid.Children.Add(stat);
    }

    private static Control Stat(string label, string value)
    {
        return new Border
        {
            Margin = new Thickness(0),
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(10),
            Background = AppearanceService.Brush(AppearanceService.SurfaceInsetBrush),
            BorderBrush = AppearanceService.Brush(AppearanceService.BorderSoftBrush),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        FontSize = AppearanceService.Font(12),
                        Foreground = AppearanceService.Brush(AppearanceService.TextMutedBrush)
                    },
                    new TextBlock
                    {
                        Text = value,
                        FontSize = AppearanceService.Font(20),
                        FontWeight = FontWeight.SemiBold,
                        Foreground = AppearanceService.Brush(AppearanceService.TextPrimaryBrush)
                    }
                }
            }
        };
    }

    private static Control Bar(double percent, IBrush color)
    {
        return new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = double.IsFinite(percent) ? percent : 0,
            Height = 8,
            Foreground = color,
            Background = AppearanceService.Brush(AppearanceService.BorderSubtleBrush)
        };
    }

    private Control TasklistRow(ProjectTasklistRow row, IBrush color, bool canReorder)
    {
        var text = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = row.Title,
                    FontSize = AppearanceService.Font(14),
                    FontWeight = FontWeight.SemiBold,
                    Foreground = AppearanceService.Brush(AppearanceService.TextPrimaryBrush),
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = row.Detail,
                    FontSize = AppearanceService.Font(12),
                    Foreground = AppearanceService.Brush(
                        row.Missing ? AppearanceService.DangerTextBrush : AppearanceService.TextMutedBrush),
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                Bar(row.Missing ? 0 : row.Percent, color)
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!row.Missing)
        {
            var open = Action(
                "Open",
                primary: true,
                () => OpenRequested?.Invoke(this, row.DocumentId),
                enabled: !row.Open);
            if (row.Open)
                ToolTip.SetTip(open, "Already open");
            buttons.Children.Add(open);
            buttons.Children.Add(Action("Move", primary: false, () => MoveRequested?.Invoke(this, row.DocumentId)));
        }

        buttons.Children.Add(Action("Remove", primary: false, () => RemoveRequested?.Invoke(this, row.DocumentId)));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(canReorder ? "Auto,*,Auto" : "*,Auto"),
            ColumnSpacing = 12
        };
        if (canReorder)
        {
            var grip = new Border
            {
                Width = 18,
                Background = Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = "⋮⋮",
                    FontSize = AppearanceService.Font(14),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = AppearanceService.Brush(AppearanceService.TextFaintBrush),
                    IsHitTestVisible = false
                }
            };
            ToolTip.SetTip(grip, "Drag to reorder");
            grid.Children.Add(grip);
            Grid.SetColumn(text, 1);
            Grid.SetColumn(buttons, 2);
        }
        else
        {
            Grid.SetColumn(buttons, 1);
        }

        grid.Children.Add(text);
        grid.Children.Add(buttons);

        var card = new Border
        {
            Tag = row.DocumentId,
            Padding = new Thickness(14, 12),
            CornerRadius = new CornerRadius(10),
            Background = AppearanceService.Brush(AppearanceService.CardBrush),
            BorderBrush = AppearanceService.Brush(AppearanceService.BorderSoftBrush),
            BorderThickness = new Thickness(1),
            Cursor = canReorder ? new Cursor(StandardCursorType.SizeAll) : new Cursor(StandardCursorType.Arrow),
            Child = grid
        };
        if (canReorder)
        {
            card.PointerPressed += (_, e) =>
            {
                if (IsInsideButton(e.Source))
                    return;

                StartTasklistDrag(card, e);
            };
        }

        return card;
    }

    private void StartTasklistDrag(Border card, PointerPressedEventArgs e)
    {
        if (_dragRow is not null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _dragRow = card;
        _dragPointer = e.Pointer;
        _dragStart = e.GetPosition(_tasklists);
        _dragging = false;
        _reordered = false;
        _pendingTarget = -1;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnTasklistPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragRow is not { } row || _dragPointer != e.Pointer)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            FinishTasklistDrag(e.Pointer);
            return;
        }

        var position = e.GetPosition(_tasklists);
        if (!_dragging)
        {
            var delta = position - _dragStart;
            if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4)
                return;

            _dragging = true;
            row.ZIndex = 2;
            row.BorderBrush = AppearanceService.Brush(AppearanceService.AccentBrush);
        }

        ScrollTasklists(e.GetPosition(_scroll));
        QueueTasklistMove(IndexAt(position.Y, row));
        e.Handled = true;
    }

    private void OnTasklistPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragRow is null || _dragPointer != e.Pointer)
            return;

        FinishTasklistDrag(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragRow is null)
            return;

        FinishTasklistDrag(null);
    }

    private void FinishTasklistDrag(IPointer? pointer)
    {
        CommitTasklistMove();
        List<Guid>? ids = null;
        if (_reordered)
        {
            ids = _tasklists.Children
                .OfType<Border>()
                .Select(card => card.Tag)
                .OfType<Guid>()
                .ToList();
        }

        ClearTasklistDrag(pointer);
        if (ids is { Count: > 1 })
            Reordered?.Invoke(this, ids);
    }

    private void ClearTasklistDrag(IPointer? pointer)
    {
        if (_dragRow is { } row)
        {
            row.ZIndex = 0;
            row.BorderBrush = AppearanceService.Brush(AppearanceService.BorderSoftBrush);
        }

        _dragRow = null;
        _dragPointer = null;
        _dragging = false;
        _reordered = false;
        _pendingTarget = -1;
        if (pointer?.Captured == this)
            pointer.Capture(null);
    }

    private void QueueTasklistMove(int target)
    {
        _pendingTarget = target;
        if (_moveQueued)
            return;

        _moveQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _moveQueued = false;
            CommitTasklistMove();
        }, DispatcherPriority.Input);
    }

    private void CommitTasklistMove()
    {
        if (_dragRow is not Border row)
            return;

        var target = _pendingTarget;
        _pendingTarget = -1;
        if (target < 0)
            return;

        var from = _tasklists.Children.IndexOf(row);
        if (from < 0 || target == from || target >= _tasklists.Children.Count)
            return;

        _tasklists.Children.RemoveAt(from);
        _tasklists.Children.Insert(target, row);
        _reordered = true;
    }

    private void ScrollTasklists(Point pointer)
    {
        var height = _scroll.Bounds.Height;
        if (height <= 0)
            return;

        var offset = _scroll.Offset.Y;
        if (pointer.Y < 28)
            offset -= 16;
        else if (pointer.Y > height - 28)
            offset += 16;
        else
            return;

        var max = _scroll.Extent.Height - height;
        if (max < 0)
            max = 0;
        if (offset < 0)
            offset = 0;
        if (offset > max)
            offset = max;
        _scroll.Offset = new Vector(0, offset);
    }

    private int IndexAt(double y, Border dragging)
    {
        var index = 0;
        foreach (var child in _tasklists.Children)
        {
            if (ReferenceEquals(child, dragging))
                continue;

            var bounds = child.Bounds;
            if (y > bounds.Y + bounds.Height / 2)
                index++;
        }

        return index;
    }

    private static bool IsInsideButton(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is Button)
                return true;
        }

        return false;
    }

    private static Button Action(string text, bool primary, Action onClick, bool danger = false, bool enabled = true)
    {
        var button = new Button
        {
            Content = text,
            IsEnabled = enabled,
            Height = 34,
            MinWidth = 76,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(12, 0),
            CornerRadius = new CornerRadius(8),
            FontSize = AppearanceService.Font(13),
            FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Opacity = enabled ? 1 : 0.45,
            Background = AppearanceService.Brush(
                danger
                    ? AppearanceService.DangerSurfaceBrush
                    : primary
                        ? AppearanceService.AccentBrush
                        : AppearanceService.CardBrush),
            Foreground = AppearanceService.Brush(
                danger
                    ? AppearanceService.DangerTextBrush
                    : primary
                        ? AppearanceService.TextOnAccentBrush
                        : AppearanceService.TextBodyBrush),
            BorderBrush = AppearanceService.Brush(
                danger
                    ? AppearanceService.DangerTextBrush
                    : primary
                        ? AppearanceService.AccentBrush
                        : AppearanceService.BorderBrush),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(enabled ? StandardCursorType.Hand : StandardCursorType.Arrow)
        };
        if (enabled)
            button.Click += (_, _) => onClick();
        return button;
    }
}
