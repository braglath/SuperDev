using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
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
    private readonly TextBox _platform;
    private readonly TextBox _framework;
    private readonly TextBlock _error;
    private string _icon;
    private string _color;
    private readonly List<Button> _iconButtons = [];
    private readonly List<Button> _colorButtons = [];

    public ProjectEditorForm(Project? existing)
    {
        _icon = ProjectAppearance.Icon(existing?.Icon);
        _color = ProjectAppearance.Color(existing?.Color);

        _name = Field(existing?.Name, "Project name");
        _description = Field(existing?.Description, "What this project is for");
        _description.AcceptsReturn = true;
        _description.MinHeight = 96;
        _description.Height = 96;
        _description.TextWrapping = TextWrapping.Wrap;
        _description.VerticalContentAlignment = VerticalAlignment.Top;
        _description.Padding = new Thickness(12, 12, 12, 10);
        _platform = Field(existing?.Platform, "iOS, Android, Web…");
        _framework = Field(existing?.Framework, "Swift, Avalonia, React…");
        _error = new TextBlock
        {
            Foreground = AppearanceService.Brush(AppearanceService.DangerTextBrush),
            FontSize = AppearanceService.Font(13),
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap
        };

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Label("Name"),
                _name,
                Label("Description"),
                _description,
                Label("Platform"),
                _platform,
                Label("Framework"),
                _framework,
                Label("Icon"),
                IconRow(),
                Label("Color"),
                ColorRow(),
                _error
            }
        };
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
            _platform.Text?.Trim() ?? string.Empty,
            _framework.Text?.Trim() ?? string.Empty,
            _icon,
            _color);
    }

    private WrapPanel IconRow()
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var icon in ProjectAppearance.Icons)
        {
            var key = icon;
            var button = new Button
            {
                Width = 40,
                Height = 40,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                Background = AppearanceService.Brush(AppearanceService.CardBrush),
                BorderBrush = AppearanceService.Brush(AppearanceService.BorderBrush),
                BorderThickness = new Thickness(1),
                Content = ProjectChrome.Icon(key, AppearanceService.Brush(AppearanceService.TextPrimaryBrush), 16),
                Cursor = new Cursor(StandardCursorType.Hand)
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

        PaintChoices();
        return row;
    }

    private WrapPanel ColorRow()
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var color in ProjectAppearance.Colors)
        {
            var key = color;
            var button = new Button
            {
                Width = 32,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(16),
                Background = ProjectChrome.Brush(key),
                BorderThickness = new Thickness(2),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(button, key);
            button.Click += (_, _) =>
            {
                _color = key;
                PaintChoices();
            };
            _colorButtons.Add(button);
            row.Children.Add(button);
        }

        PaintChoices();
        return row;
    }

    private void PaintChoices()
    {
        for (var i = 0; i < _iconButtons.Count; i++)
        {
            var selected = ProjectAppearance.Icons[i] == _icon;
            _iconButtons[i].BorderBrush = AppearanceService.Brush(
                selected ? AppearanceService.AccentBrush : AppearanceService.BorderBrush);
            _iconButtons[i].BorderThickness = new Thickness(selected ? 2 : 1);
        }

        for (var i = 0; i < _colorButtons.Count; i++)
        {
            var selected = ProjectAppearance.Colors[i] == _color;
            _colorButtons[i].BorderBrush = AppearanceService.Brush(
                selected ? AppearanceService.TextPrimaryBrush : AppearanceService.BorderSoftBrush);
        }
    }

    private static TextBlock Label(string text) =>
        new()
        {
            Text = text,
            FontSize = AppearanceService.Font(13),
            FontWeight = FontWeight.SemiBold,
            Foreground = AppearanceService.Brush(AppearanceService.TextBodyBrush)
        };

    private static TextBox Field(string? text, string watermark) =>
        new()
        {
            Text = text ?? string.Empty,
            PlaceholderText = watermark,
            Height = 40,
            Padding = new Thickness(12, 10),
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(10),
            Background = AppearanceService.Brush(AppearanceService.SurfaceInsetBrush),
            Foreground = AppearanceService.Brush(AppearanceService.TextPrimaryBrush),
            BorderBrush = AppearanceService.Brush(AppearanceService.BorderBrush),
            BorderThickness = new Thickness(1)
        };
}

public sealed class ProjectEditorWindow : Window
{
    private readonly ProjectEditorForm _form;

    private ProjectEditorWindow(Project? existing)
    {
        Title = existing is null ? "Create project" : "Edit project";
        Width = 520;
        Height = 680;
        MinWidth = 440;
        MinHeight = 560;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppearanceService.Brush(AppearanceService.AppBackgroundBrush);
        _form = new ProjectEditorForm(existing);

        var save = new Button
        {
            Content = existing is null ? "Create project" : "Save",
            Height = 40,
            MinWidth = 120,
            Padding = new Thickness(16, 0),
            CornerRadius = new CornerRadius(10),
            Background = AppearanceService.Brush(AppearanceService.AccentBrush),
            Foreground = AppearanceService.Brush(AppearanceService.TextOnAccentBrush),
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
            Height = 40,
            MinWidth = 96,
            Padding = new Thickness(16, 0),
            CornerRadius = new CornerRadius(10),
            Background = AppearanceService.Brush(AppearanceService.CardBrush),
            Foreground = AppearanceService.Brush(AppearanceService.TextBodyBrush),
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
