using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SuperDev.Controls;
using SuperDev.Models;
using SuperDev.Services;

namespace SuperDev.Views;

/// <summary>
/// The project name, icon, and color form. Hosted on the create/edit page
/// and, if needed, inside <see cref="ProjectEditorWindow"/>.
/// </summary>
public sealed class ProjectEditorForm : UserControl
{
    private readonly TextBox _name;
    private readonly TextBox _description;
    private readonly ComboBox _platform;
    private readonly ComboBox _framework;
    private readonly TextBlock _error;
    private string _icon;
    private string _color;
    private readonly List<Button> _iconButtons = [];
    private readonly List<Ellipse> _colorRings = [];

    public ProjectEditorForm(Project? existing)
    {
        _icon = ProjectAppearance.Icon(existing?.Icon);
        _color = ProjectAppearance.Color(existing?.Color);

        _name = Field(existing?.Name, "Enter project name");
        _description = Field(existing?.Description, "Briefly describe what this project is for...");
        _description.AcceptsReturn = true;
        _description.MinHeight = 96;
        _description.TextWrapping = TextWrapping.Wrap;
        _description.VerticalContentAlignment = VerticalAlignment.Top;
        _platform = Choice(existing?.Platform, PlatformOptions(), "Select platform");
        _framework = Choice(existing?.Framework, FrameworkOptions(), "Select framework");
        _error = new TextBlock
        {
            Foreground = AppearanceService.Brush(AppearanceService.DangerTextBrush),
            FontSize = AppearanceService.Font(13),
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap
        };

        var stack = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 16
        };
        var platform = Group("Platform", Shell(_platform, Glyph(Phone)));
        var framework = Group("Framework", Shell(_framework, Glyph(Layers)));
        Grid.SetColumn(framework, 1);
        stack.Children.Add(platform);
        stack.Children.Add(framework);

        Content = new StackPanel
        {
            Spacing = 22,
            Children =
            {
                Group("Project Name", Shell(_name, Glyph(Folder)), required: true, error: _error),
                Group("Description", Shell(_description, Glyph(Document), alignTop: true)),
                stack,
                Group("Project Icon", IconRow()),
                Group("Project Color", ColorRow())
            }
        };
        PaintChoices();
    }

    public ProjectDraft? ReadDraft()
    {
        var name = _name.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            _error.Text = "Enter a project name.";
            _error.IsVisible = true;
            _name.Focus();
            return null;
        }

        _error.IsVisible = false;
        return new ProjectDraft(
            name,
            _description.Text?.Trim() ?? string.Empty,
            ReadChoice(_platform),
            ReadChoice(_framework),
            _icon,
            _color);
    }

    private WrapPanel IconRow()
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var icon in IconChoices())
        {
            var key = icon;
            var button = new Button
            {
                Width = 48,
                Height = 48,
                MinWidth = 0,
                MinHeight = 0,
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                Content = ProjectChrome.Icon(key, AppearanceService.Brush(AppearanceService.TextPrimaryBrush), 18),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = key
            };
            ToolTip.SetTip(button, key);
            button.Click += (_, _) =>
            {
                _icon = key;
                PaintChoices();
            };
            _iconButtons.Add(button);
            row.Children.Add(button);
        }

        return row;
    }

    private WrapPanel ColorRow()
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var color in ProjectAppearance.Colors)
        {
            var key = color;
            var ring = new Ellipse
            {
                Width = 32,
                Height = 32,
                StrokeThickness = 2,
                Fill = Brushes.Transparent,
                IsVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var dot = new Ellipse
            {
                Width = 22,
                Height = 22,
                Fill = ProjectChrome.Brush(key),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var button = new Button
            {
                Width = 36,
                Height = 36,
                MinWidth = 0,
                MinHeight = 0,
                Margin = new Thickness(0, 0, 12, 8),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Content = new Grid
                {
                    Width = 36,
                    Height = 36,
                    Children = { ring, dot }
                },
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(button, key);
            button.Click += (_, _) =>
            {
                _color = key;
                PaintChoices();
            };
            _colorRings.Add(ring);
            row.Children.Add(button);
        }

        return row;
    }

    private void PaintChoices()
    {
        foreach (var button in _iconButtons)
        {
            var selected = button.Tag as string == _icon;
            button.Background = AppearanceService.Brush(
                selected ? AppearanceService.AccentSubtleBrush : AppearanceService.SurfaceBrush);
            button.BorderBrush = AppearanceService.Brush(
                selected ? AppearanceService.AccentBrush : AppearanceService.BorderBrush);
            button.BorderThickness = new Thickness(selected ? 2 : 1);
            if (button.Content is Viewbox view && view.Child is Path path)
            {
                path.Stroke = AppearanceService.Brush(
                    selected ? AppearanceService.AccentTextBrush : AppearanceService.TextPrimaryBrush);
            }
        }

        for (var i = 0; i < _colorRings.Count; i++)
        {
            var selected = ProjectAppearance.Colors[i] == _color;
            _colorRings[i].IsVisible = selected;
            _colorRings[i].Stroke = ProjectChrome.Brush(ProjectAppearance.Colors[i]);
        }
    }

    private static Control Group(string text, Control field, bool required = false, TextBlock? error = null)
    {
        var group = new StackPanel
        {
            Spacing = 8,
            Children = { Label(text, required), field }
        };
        if (error is not null)
            group.Children.Add(error);
        return group;
    }

    private static Control Label(string text, bool required)
    {
        var title = new TextBlock
        {
            Text = text,
            FontSize = AppearanceService.Font(14),
            FontWeight = FontWeight.SemiBold,
            Foreground = AppearanceService.Brush(AppearanceService.TextPrimaryBrush),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!required)
            return title;

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            Children =
            {
                title,
                new TextBlock
                {
                    Text = "*",
                    FontSize = AppearanceService.Font(14),
                    FontWeight = FontWeight.SemiBold,
                    Foreground = AppearanceService.Brush(AppearanceService.DangerBrush),
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
    }

    private static TextBox Field(string? text, string watermark)
    {
        var box = new TextBox
        {
            Text = text ?? string.Empty,
            PlaceholderText = watermark,
            PlaceholderForeground = AppearanceService.Brush(AppearanceService.TextFaintBrush),
            FontSize = AppearanceService.Font(14),
            Padding = new Thickness(4, 12, 12, 12),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Foreground = AppearanceService.Brush(AppearanceService.TextPrimaryBrush),
            BorderThickness = new Thickness(0)
        };
        TextBoxClearButton.SetShow(box, false);
        return box;
    }

    private static ComboBox Choice(string? current, IReadOnlyList<string> options, string placeholder)
    {
        var items = WithCurrent(options, current, out var selected);
        var box = new ComboBox
        {
            PlaceholderText = placeholder,
            PlaceholderForeground = AppearanceService.Brush(AppearanceService.TextFaintBrush),
            ItemsSource = items,
            FontSize = AppearanceService.Font(14),
            Foreground = AppearanceService.Brush(AppearanceService.TextPrimaryBrush),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinHeight = 48,
            MaxDropDownHeight = 280,
            Padding = new Thickness(2, 0, 8, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        if (selected is not null)
        {
            box.SelectedItem = selected;
            box.Text = selected;
        }

        return box;
    }

    private static List<string> WithCurrent(IReadOnlyList<string> options, string? current, out string? selected)
    {
        var items = new List<string>();
        foreach (var option in options)
        {
            if (!items.Exists(item => string.Equals(item, option, StringComparison.OrdinalIgnoreCase)))
                items.Add(option);
        }

        selected = null;
        var value = current?.Trim() ?? string.Empty;
        if (value.Length == 0)
            return items;

        selected = items.Find(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            items.Insert(0, value);
            selected = value;
        }

        return items;
    }

    private static string ReadChoice(ComboBox box) =>
        (box.SelectedItem as string ?? box.Text ?? string.Empty).Trim();

    private IEnumerable<string> IconChoices()
    {
        foreach (var icon in ProjectAppearance.Icons)
            yield return icon;

        if (Array.Exists(ProjectAppearance.LegacyIcons, icon => icon == _icon))
            yield return _icon;
    }

    private static IReadOnlyList<string> PlatformOptions()
    {
        var labels = new List<string>();
        foreach (var choice in AiCatalog.Platforms)
            labels.Add(choice.Label);
        return labels;
    }

    private static IReadOnlyList<string> FrameworkOptions()
    {
        var labels = new List<string>();
        foreach (DevFramework framework in Enum.GetValues<DevFramework>())
        {
            if (framework == DevFramework.Custom)
                labels.Add("Avalonia");
            labels.Add(AiCatalog.FrameworkLabel(framework));
        }

        return labels;
    }

    private static Border Shell(Control input, Control icon, bool alignTop = false)
    {
        icon.Margin = alignTop ? new Thickness(14, 16, 0, 0) : new Thickness(14, 0, 0, 0);
        icon.VerticalAlignment = alignTop ? VerticalAlignment.Top : VerticalAlignment.Center;
        icon.IsHitTestVisible = false;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("40,*"),
            MinHeight = alignTop ? 112 : 48
        };
        Grid.SetColumn(input, 1);
        grid.Children.Add(icon);
        grid.Children.Add(input);

        var border = new Border
        {
            Background = AppearanceService.Brush(AppearanceService.SurfaceBrush),
            BorderBrush = AppearanceService.Brush(AppearanceService.BorderBrush),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            MinHeight = alignTop ? 112 : 50,
            Child = grid
        };
        var idle = AppearanceService.Brush(AppearanceService.BorderBrush);
        var focus = AppearanceService.Brush(AppearanceService.AccentBrush);
        input.GotFocus += (_, _) => border.BorderBrush = focus;
        input.LostFocus += (_, _) => border.BorderBrush = idle;
        if (input is ComboBox combo)
        {
            combo.DropDownOpened += (_, _) => border.BorderBrush = focus;
            combo.DropDownClosed += (_, _) =>
            {
                if (!combo.IsFocused)
                    border.BorderBrush = idle;
            };
        }

        return border;
    }

    private static Control Glyph(string data) =>
        new Viewbox
        {
            Width = 16,
            Height = 16,
            Child = new Path
            {
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                Stroke = AppearanceService.Brush(AppearanceService.TextMutedBrush),
                StrokeThickness = 1.6,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent,
                Data = Geometry.Parse(data)
            }
        };

    private const string Folder = "M2,5 H6.2 L7.6,3.2 H14 V12.8 H2 Z";
    private const string Document = "M4,1.8 H9.2 L12.2,4.8 V14.2 H4 Z M9.2,1.8 V4.8 H12.2";
    private const string Phone = "M6,1.8 H10 C10.8,1.8 11.4,2.4 11.4,3.2 V12.8 C11.4,13.6 10.8,14.2 10,14.2 H6 C5.2,14.2 4.6,13.6 4.6,12.8 V3.2 C4.6,2.4 5.2,1.8 6,1.8 Z M7.2,12.2 H8.8";
    private const string Layers = "M8,2.2 L14,5.2 L8,8.2 L2,5.2 Z M3.2,7.6 L8,10.6 L12.8,7.6 M3.2,10.2 L8,13.2 L12.8,10.2";
}

public sealed class ProjectEditorWindow : Window
{
    private readonly ProjectEditorForm _form;

    private ProjectEditorWindow(Project? existing)
    {
        Title = existing is null ? "Create Project" : "Edit Project";
        Width = 640;
        Height = 760;
        MinWidth = 520;
        MinHeight = 640;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppearanceService.Brush(AppearanceService.AppBackgroundBrush);
        _form = new ProjectEditorForm(existing);

        var save = new Button
        {
            Content = existing is null ? "Create Project" : "Save",
            Height = 44,
            MinWidth = 140,
            Padding = new Thickness(18, 0),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.Parse("#22C55E")),
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        save.Click += (_, _) =>
        {
            var draft = _form.ReadDraft();
            if (draft is not null)
                Close(draft);
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Height = 44,
            MinWidth = 110,
            Padding = new Thickness(18, 0),
            CornerRadius = new CornerRadius(12),
            Background = AppearanceService.Brush(AppearanceService.SurfaceBrush),
            Foreground = AppearanceService.Brush(AppearanceService.TextPrimaryBrush),
            BorderBrush = AppearanceService.Brush(AppearanceService.BorderBrush),
            BorderThickness = new Thickness(1),
            FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        cancel.Click += (_, _) => Close(null);

        var root = new StackPanel
        {
            Spacing = 16,
            Margin = new Thickness(24, 22, 24, 24),
            Children =
            {
                _form,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, save }
                }
            }
        };
        Content = new ScrollViewer
        {
            Content = root,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
    }

    public static Task<ProjectDraft?> PickAsync(Window owner, Project? existing)
    {
        var window = new ProjectEditorWindow(existing);
        return window.ShowDialog<ProjectDraft?>(owner);
    }
}

public sealed record TasklistChoice(string Title, string Detail, Guid DocumentId, string? Path, bool Browse);

public sealed class TasklistPickerWindow : Window
{
    private TasklistPickerWindow(string title, IReadOnlyList<TasklistChoice> choices, bool allowBrowse)
    {
        Title = title;
        Width = 480;
        Height = 520;
        MinWidth = 380;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppearanceService.Brush(AppearanceService.AppBackgroundBrush);

        var list = new StackPanel { Spacing = 8 };
        if (choices.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "No checklists to show yet.",
                Foreground = AppearanceService.Brush(AppearanceService.TextMutedBrush),
                TextWrapping = TextWrapping.Wrap
            });
        }

        foreach (var choice in choices)
        {
            var captured = choice;
            var button = ChoiceButton(choice.Title, choice.Detail);
            button.Click += (_, _) => Close(captured);
            list.Children.Add(button);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        if (allowBrowse)
        {
            var browse = ChoiceButton("Choose a file…", "Open a checklist you already saved");
            browse.Click += (_, _) => Close(new TasklistChoice(string.Empty, string.Empty, Guid.Empty, null, true));
            buttons.Children.Add(browse);
        }

        var cancel = ChoiceButton("Cancel", string.Empty);
        cancel.Click += (_, _) => Close(null);
        buttons.Children.Add(cancel);

        Content = new DockPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                buttons,
                new ScrollViewer
                {
                    Content = list,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                }
            }
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
    }

    public static Task<TasklistChoice?> PickAsync(
        Window owner,
        string title,
        IReadOnlyList<TasklistChoice> choices,
        bool allowBrowse)
    {
        var window = new TasklistPickerWindow(title, choices, allowBrowse);
        return window.ShowDialog<TasklistChoice?>(owner);
    }

    private static Button ChoiceButton(string title, string detail)
    {
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            Foreground = AppearanceService.Brush(AppearanceService.TextPrimaryBrush),
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(detail))
        {
            body.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = AppearanceService.Font(12),
                Foreground = AppearanceService.Brush(AppearanceService.TextMutedBrush),
                TextWrapping = TextWrapping.Wrap
            });
        }

        return new Button
        {
            Content = body,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = AppearanceService.Brush(AppearanceService.SurfaceInsetBrush),
            BorderBrush = AppearanceService.Brush(AppearanceService.BorderSoftBrush),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
    }
}
