using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SuperDev.Services;

namespace SuperDev.Views;

public sealed record ReminderEditResult(bool Removed, DateTime NextUtc, int EveryMinutes, bool Silent);

public sealed class EditReminderWindow : Window
{
    private readonly bool _scheduleEditable;
    private readonly TextBox _whenBox;
    private readonly CheckBox _repeatBox;
    private readonly TextBox _intervalBox;
    private readonly ComboBox _unitBox;
    private readonly CheckBox _silentBox;
    private readonly TextBlock _error;
    private readonly StackPanel _schedulePanel;

    public EditReminderWindow(ScheduledReminder reminder, bool scheduleEditable)
    {
        _scheduleEditable = scheduleEditable;
        Title = "Edit reminder";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#0B1120");

        var local = reminder.NextOn.ToLocalTime();
        _whenBox = Field(local.ToString("dd MMM yyyy h:mm tt", CultureInfo.InvariantCulture));
        _repeatBox = new CheckBox
        {
            Content = "Repeat",
            IsChecked = reminder.EveryMinutes > 0,
            Foreground = Brush("#E2E8F0"),
            Margin = new Thickness(0, 12, 0, 0)
        };
        _intervalBox = Field(RepeatCount(reminder.EveryMinutes).ToString(CultureInfo.InvariantCulture));
        _intervalBox.Width = 72;
        _unitBox = new ComboBox { Width = 120, SelectedIndex = RepeatUnit(reminder.EveryMinutes) };
        _unitBox.Items.Add("minutes");
        _unitBox.Items.Add("hours");
        _unitBox.Items.Add("days");
        _silentBox = new CheckBox
        {
            Content = "Silent",
            IsChecked = reminder.Silent,
            Foreground = Brush("#E2E8F0"),
            Margin = new Thickness(0, 12, 0, 0)
        };
        _error = new TextBlock
        {
            Foreground = Brush("#F87171"),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var repeatRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { _intervalBox, _unitBox }
        };
        _schedulePanel = new StackPanel
        {
            IsVisible = scheduleEditable,
            Children =
            {
                Label("When"),
                _whenBox,
                _repeatBox,
                repeatRow
            }
        };

        var whenText = new TextBlock
        {
            Text = ReminderText.Clock(reminder.NextOn),
            Foreground = Brush("#F8FAFC"),
            FontSize = 15,
            IsVisible = !scheduleEditable
        };
        var note = new TextBlock
        {
            Text = scheduleEditable
                ? "Example: 16 Oct 2026 9:30 AM"
                : "This reminder follows the checklist deadline.",
            Foreground = Brush("#64748B"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var remove = DialogButton("Remove", "#3F1D1D", "#FECACA");
        remove.Click += (_, _) => Close(new ReminderEditResult(true, default, 0, false));
        var cancel = DialogButton("Cancel", "#172033", "#E2E8F0");
        cancel.Click += (_, _) => Close(null);
        var save = DialogButton("Save", "#2563EB", "#FFFFFF");
        save.Click += (_, _) => Save(reminder);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 18, 0, 0),
            Children = { remove, cancel, save }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(reminder.Title) ? "Reminder" : reminder.Title,
                    Foreground = Brush("#F8FAFC"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = Kind(reminder),
                    Foreground = Brush("#94A3B8"),
                    FontSize = 13,
                    Margin = new Thickness(0, 4, 0, 14)
                },
                whenText,
                _schedulePanel,
                note,
                _silentBox,
                _error,
                buttons
            }
        };
    }

    private void Save(ScheduledReminder reminder)
    {
        if (!_scheduleEditable)
        {
            Close(new ReminderEditResult(false, reminder.NextOn, 0, _silentBox.IsChecked == true));
            return;
        }

        if (!DateTime.TryParseExact(
                _whenBox.Text?.Trim(),
                "dd MMM yyyy h:mm tt",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var local))
        {
            ShowError("Enter the time as 16 Oct 2026 9:30 AM.");
            return;
        }

        var every = 0;
        if (_repeatBox.IsChecked == true)
        {
            if (!int.TryParse(_intervalBox.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                count is < 1 or > 999)
            {
                ShowError("Enter a repeat interval from 1 to 999.");
                return;
            }

            every = _unitBox.SelectedIndex switch
            {
                2 => count * 1440,
                1 => count * 60,
                _ => count
            };
        }

        Close(new ReminderEditResult(false, local.ToUniversalTime(), every, _silentBox.IsChecked == true));
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    private static int RepeatCount(int minutes)
    {
        if (minutes <= 0)
            return 1;
        if (minutes % 1440 == 0)
            return minutes / 1440;
        if (minutes % 60 == 0)
            return minutes / 60;
        return minutes;
    }

    private static int RepeatUnit(int minutes)
    {
        if (minutes > 0 && minutes % 1440 == 0)
            return 2;
        if (minutes > 0 && minutes % 60 == 0)
            return 1;
        return 0;
    }

    private static string Kind(ScheduledReminder reminder)
    {
        if (string.IsNullOrEmpty(reminder.Slot))
            return "Checklist reminder";
        if (reminder.Slot == ScheduledReminder.DeadlineSlot)
            return "Deadline";
        return "Check deadline";
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = Brush("#94A3B8"),
        FontSize = 13,
        Margin = new Thickness(0, 0, 0, 6)
    };

    private static TextBox Field(string text) => new()
    {
        Text = text,
        Background = Brush("#111827"),
        Foreground = Brush("#F8FAFC"),
        BorderBrush = Brush("#334155"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 8),
        FontSize = 14
    };

    private static Button DialogButton(string content, string background, string foreground) => new()
    {
        Content = content,
        Background = Brush(background),
        Foreground = Brush(foreground),
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(14, 0),
        Height = 36,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));
}
