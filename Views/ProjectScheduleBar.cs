using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SuperDev.Models;
using SuperDev.Services;

namespace SuperDev.Views;

/// <summary>
/// Deadline and reminder controls for a project, matching the checklist header.
/// </summary>
public sealed class ProjectScheduleBar : StackPanel
{
    private readonly Project _project;
    private readonly Action _changed;

    private readonly Button _deadlineButton;
    private readonly Button _clearDeadline;
    private readonly Button _reminderButton;
    private readonly Button _clearReminder;

    private readonly Avalonia.Controls.Calendar _deadlineCalendar;
    private readonly TextBox _deadlineHour;
    private readonly TextBox _deadlineMinute;
    private readonly TextBox _deadlineLead;
    private readonly TextBlock _deadlineError;
    private readonly StackPanel _deadlineLeadPanel;
    private readonly Toggle _deadlineAm;
    private readonly Toggle _deadlinePm;
    private readonly Toggle _notifyNone;
    private readonly Toggle _notifyOn;
    private readonly Toggle _notifyBefore;
    private readonly Toggle _leadMinutes;
    private readonly Toggle _leadHours;
    private readonly Toggle _deadlineSilent;
    private readonly Flyout _deadlineFlyout;

    private readonly Avalonia.Controls.Calendar _reminderCalendar;
    private readonly TextBox _reminderHour;
    private readonly TextBox _reminderMinute;
    private readonly TextBox _reminderInterval;
    private readonly TextBlock _reminderError;
    private readonly StackPanel _reminderOnce;
    private readonly StackPanel _reminderRepeat;
    private readonly Toggle _reminderAm;
    private readonly Toggle _reminderPm;
    private readonly Toggle _once;
    private readonly Toggle _repeat;
    private readonly Toggle _intervalMinutes;
    private readonly Toggle _intervalHours;
    private readonly Toggle _intervalDays;
    private readonly Toggle _reminderSilent;
    private readonly Flyout _reminderFlyout;

    public ProjectScheduleBar(Project project, Action changed)
    {
        _project = project;
        _changed = changed;
        Orientation = Orientation.Horizontal;
        Spacing = 0;

        _deadlineCalendar = new Avalonia.Controls.Calendar { SelectionMode = CalendarSelectionMode.SingleDate };
        _deadlineHour = TimeBox("09");
        _deadlineMinute = TimeBox("00");
        _deadlineLead = TimeBox("30", 72);
        _deadlineError = Error();
        _deadlineAm = new Toggle("AM");
        _deadlinePm = new Toggle("PM");
        _notifyNone = new Toggle("None");
        _notifyOn = new Toggle("On time");
        _notifyBefore = new Toggle("Before");
        _leadMinutes = new Toggle("minutes");
        _leadHours = new Toggle("hours");
        _deadlineSilent = new Toggle("Silent");
        _deadlineAm.OnClick(() => SetPair(_deadlineAm, _deadlinePm, true));
        _deadlinePm.OnClick(() => SetPair(_deadlineAm, _deadlinePm, false));
        _notifyNone.OnClick(() => ShowNotify(0));
        _notifyOn.OnClick(() => ShowNotify(1));
        _notifyBefore.OnClick(() => ShowNotify(2, _leadHours.Selected));
        _leadMinutes.OnClick(() => ShowLeadUnit(hours: false));
        _leadHours.OnClick(() => ShowLeadUnit(hours: true));
        _deadlineSilent.OnClick(() => _deadlineSilent.Set(!_deadlineSilent.Selected));
        _deadlineSilent.Button.HorizontalAlignment = HorizontalAlignment.Left;
        _deadlineLeadPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            IsVisible = false,
            Children = { _deadlineLead, _leadMinutes.Button, _leadHours.Button }
        };
        _deadlineFlyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = DeadlineEditor()
        };
        _deadlineFlyout.Opened += (_, _) => LoadDeadline();

        _reminderCalendar = new Avalonia.Controls.Calendar { SelectionMode = CalendarSelectionMode.SingleDate };
        _reminderHour = TimeBox("09");
        _reminderMinute = TimeBox("00");
        _reminderInterval = TimeBox("1", 72);
        _reminderError = Error();
        _reminderAm = new Toggle("AM");
        _reminderPm = new Toggle("PM");
        _once = new Toggle("One time");
        _repeat = new Toggle("Occurrence");
        _intervalMinutes = new Toggle("minutes");
        _intervalHours = new Toggle("hours");
        _intervalDays = new Toggle("days");
        _reminderSilent = new Toggle("Silent");
        _reminderAm.OnClick(() => SetPair(_reminderAm, _reminderPm, true));
        _reminderPm.OnClick(() => SetPair(_reminderAm, _reminderPm, false));
        _once.OnClick(() => ShowReminderMode(repeating: false));
        _repeat.OnClick(() => ShowReminderMode(repeating: true));
        _intervalMinutes.OnClick(() => ShowIntervalUnit(0));
        _intervalHours.OnClick(() => ShowIntervalUnit(1));
        _intervalDays.OnClick(() => ShowIntervalUnit(2));
        _reminderSilent.OnClick(() => _reminderSilent.Set(!_reminderSilent.Selected));
        _reminderSilent.Button.HorizontalAlignment = HorizontalAlignment.Left;
        _reminderOnce = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                _reminderCalendar,
                Caption("Time"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _reminderHour, Colon(), _reminderMinute, _reminderAm.Button, _reminderPm.Button }
                }
            }
        };
        _reminderRepeat = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            IsVisible = false,
            Children = { _reminderInterval, _intervalMinutes.Button, _intervalHours.Button, _intervalDays.Button }
        };
        _reminderFlyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = ReminderEditor()
        };
        _reminderFlyout.Opened += (_, _) => LoadReminder();

        _deadlineButton = Link("Pick deadline", _deadlineFlyout);
        _clearDeadline = Clear("Clear deadline", ClearDeadline);
        _reminderButton = Link("Set reminder", _reminderFlyout);
        _clearReminder = Clear("Clear reminder", ClearReminder);

        Children.Add(_deadlineButton);
        Children.Add(_clearDeadline);
        Children.Add(new TextBlock
        {
            Text = " · ",
            FontSize = AppearanceService.Font(13),
            Foreground = Brush(AppearanceService.TextMutedBrush),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        });
        Children.Add(_reminderButton);
        Children.Add(_clearReminder);
        RefreshLabels();
    }

    private Control DeadlineEditor()
    {
        var notify = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            ColumnSpacing = 8
        };
        notify.Children.Add(_notifyNone.Button);
        Grid.SetColumn(_notifyOn.Button, 1);
        notify.Children.Add(_notifyOn.Button);
        Grid.SetColumn(_notifyBefore.Button, 2);
        notify.Children.Add(_notifyBefore.Button);

        var root = new StackPanel
        {
            Spacing = 10,
            Width = 294,
            Children =
            {
                _deadlineCalendar,
                Caption("Time"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _deadlineHour, Colon(), _deadlineMinute, _deadlineAm.Button, _deadlinePm.Button }
                },
                Caption("Notify"),
                notify,
                _deadlineLeadPanel,
                _deadlineSilent.Button,
                new TextBlock
                {
                    Text = "No sound for this deadline",
                    FontSize = AppearanceService.Font(11),
                    Foreground = Brush(AppearanceService.TextSubtleBrush)
                },
                _deadlineError,
                Actions(HideDeadline, CommitDeadline)
            }
        };
        root.KeyDown += (_, e) => EditorKey(e, HideDeadline, CommitDeadline);
        return root;
    }

    private Control ReminderEditor()
    {
        var mode = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 8
        };
        mode.Children.Add(_once.Button);
        Grid.SetColumn(_repeat.Button, 1);
        mode.Children.Add(_repeat.Button);

        var root = new StackPanel
        {
            Spacing = 10,
            Width = 294,
            Children =
            {
                Caption("Repeat"),
                mode,
                _reminderOnce,
                _reminderRepeat,
                _reminderSilent.Button,
                new TextBlock
                {
                    Text = "No sound for this reminder",
                    FontSize = AppearanceService.Font(11),
                    Foreground = Brush(AppearanceService.TextSubtleBrush)
                },
                _reminderError,
                Actions(HideReminder, CommitReminder)
            }
        };
        root.KeyDown += (_, e) => EditorKey(e, HideReminder, CommitReminder);
        return root;
    }

    private void LoadDeadline()
    {
        _deadlineError.IsVisible = false;
        var selected = _project.Deadline?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
        _deadlineCalendar.SelectedDate = selected;
        _deadlineCalendar.DisplayDate = selected;

        DeadlineValue.SplitClock(_project.DeadlineMinute ?? (9 * 60), out var hour, out var minute, out var pm);
        _deadlineHour.Text = hour.ToString(CultureInfo.InvariantCulture);
        _deadlineMinute.Text = minute.ToString("00", CultureInfo.InvariantCulture);
        SetPair(_deadlineAm, _deadlinePm, !pm);

        if (_project.NotifyBeforeMinutes is int lead && lead > 0)
        {
            DeadlineValue.SplitLead(lead, out var count, out var unit);
            _deadlineLead.Text = count.ToString(CultureInfo.InvariantCulture);
            ShowNotify(2, unit == 1);
        }
        else
        {
            _deadlineLead.Text = "30";
            ShowNotify(_project.NotifyBeforeMinutes == 0 ? 1 : 0);
        }

        _deadlineSilent.Set(_project.DeadlineSilent);
    }

    private void LoadReminder()
    {
        _reminderError.IsVisible = false;
        var local = _project.ReminderOn?.ToLocalTime();
        var selected = local?.Date ?? DateTime.Today;
        _reminderCalendar.SelectedDate = selected;
        _reminderCalendar.DisplayDate = selected;

        var hour = local?.Hour ?? 9;
        _reminderHour.Text = (hour % 12 == 0 ? 12 : hour % 12).ToString(CultureInfo.InvariantCulture);
        _reminderMinute.Text = (local?.Minute ?? 0).ToString("00", CultureInfo.InvariantCulture);
        SetPair(_reminderAm, _reminderPm, hour < 12);
        _reminderSilent.Set(_project.ReminderSilent);

        if (_project.ReminderEveryMinutes > 0)
        {
            SplitInterval(_project.ReminderEveryMinutes, out var count, out var unit);
            _reminderInterval.Text = count.ToString(CultureInfo.InvariantCulture);
            ShowReminderMode(repeating: true);
            ShowIntervalUnit(unit);
        }
        else
        {
            _reminderInterval.Text = "1";
            ShowReminderMode(repeating: false);
            ShowIntervalUnit(1);
        }
    }

    private void CommitDeadline()
    {
        if (_deadlineCalendar.SelectedDate is not { } selected)
        {
            ShowError(_deadlineError, "Pick a date.");
            return;
        }

        if (!DeadlineValue.TryClock(
                _deadlineHour.Text,
                _deadlineMinute.Text,
                _deadlinePm.Selected,
                out var minute,
                out var hourLabel,
                out var pm,
                out var error))
        {
            ShowError(_deadlineError, error);
            return;
        }

        int? lead = null;
        if (_notifyBefore.Selected)
        {
            if (!DeadlineValue.TryLead(_deadlineLead.Text, _leadHours.Selected ? 1 : 0, out var before, out error))
            {
                ShowError(_deadlineError, error);
                return;
            }

            lead = before;
        }
        else if (_notifyOn.Selected)
        {
            lead = 0;
        }

        _deadlineHour.Text = hourLabel;
        _deadlineMinute.Text = (minute % 60).ToString("00", CultureInfo.InvariantCulture);
        SetPair(_deadlineAm, _deadlinePm, !pm);
        _deadlineError.IsVisible = false;
        HideDeadline();

        var next = DeadlineValue.FromCalendar(selected);
        var silent = _deadlineSilent.Selected;
        if (_project.Deadline == next &&
            _project.DeadlineMinute == minute &&
            _project.NotifyBeforeMinutes == lead &&
            _project.DeadlineSilent == silent)
            return;

        _project.Deadline = next;
        _project.DeadlineMinute = minute;
        _project.NotifyBeforeMinutes = lead;
        _project.DeadlineSilent = silent;
        RefreshLabels();
        _changed();
    }

    private void CommitReminder()
    {
        if (!TryReadReminder(out var next, out var every, out var error))
        {
            ShowError(_reminderError, error);
            return;
        }

        var silent = _reminderSilent.Selected;
        _reminderError.IsVisible = false;
        HideReminder();
        if (_project.ReminderOn == next && _project.ReminderEveryMinutes == every && _project.ReminderSilent == silent)
            return;

        _project.ReminderOn = next;
        _project.ReminderEveryMinutes = every;
        _project.ReminderSilent = silent;
        RefreshLabels();
        _changed();
    }

    private bool TryReadReminder(out DateTime nextUtc, out int everyMinutes, out string error)
    {
        nextUtc = default;
        everyMinutes = 0;
        error = string.Empty;

        if (_repeat.Selected)
        {
            if (!int.TryParse(_reminderInterval.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                count is < 1 or > 999)
            {
                error = "Enter a repeat interval from 1 to 999.";
                return false;
            }

            everyMinutes = _intervalDays.Selected ? count * 1440 : _intervalHours.Selected ? count * 60 : count;
            if (_project.ReminderEveryMinutes == everyMinutes &&
                _project.ReminderOn is { } existing &&
                existing > DateTime.UtcNow)
            {
                nextUtc = existing;
                return true;
            }

            var localNow = DateTime.Now;
            var start = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, localNow.Minute, 0, DateTimeKind.Local);
            nextUtc = start.AddMinutes(everyMinutes).ToUniversalTime();
            return true;
        }

        if (!TryReadClock(_reminderHour, _reminderMinute, _reminderPm.Selected, out var time))
        {
            error = "Enter an hour from 1 to 12, or 00 to 23, and minutes from 00 to 59.";
            return false;
        }

        var day = _reminderCalendar.SelectedDate?.Date ?? DateTime.Today;
        var local = new DateTime(day.Year, day.Month, day.Day, time.Hour, time.Minute, 0, DateTimeKind.Local);
        nextUtc = local.ToUniversalTime();
        return true;
    }

    private static bool TryReadClock(TextBox hourBox, TextBox minuteBox, bool pm, out TimeOnly time)
    {
        time = default;
        if (!int.TryParse(hourBox.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var hour))
            return false;
        if (!int.TryParse(minuteBox.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minute) ||
            minute is < 0 or > 59)
            return false;

        if (hour is >= 0 and <= 23 && (hour > 12 || hour == 0))
        {
            time = new TimeOnly(hour, minute);
            return true;
        }

        if (hour is < 1 or > 12)
            return false;

        var hour24 = hour % 12;
        if (pm)
            hour24 += 12;
        time = new TimeOnly(hour24, minute);
        return true;
    }

    private void ClearDeadline()
    {
        if (_project.Deadline is null && _project.DeadlineMinute is null && _project.NotifyBeforeMinutes is null && !_project.DeadlineSilent)
            return;

        _project.Deadline = null;
        _project.DeadlineMinute = null;
        _project.NotifyBeforeMinutes = null;
        _project.DeadlineSilent = false;
        RefreshLabels();
        _changed();
    }

    private void ClearReminder()
    {
        if (_project.ReminderOn is null && _project.ReminderEveryMinutes == 0 && !_project.ReminderSilent)
            return;

        _project.ReminderOn = null;
        _project.ReminderEveryMinutes = 0;
        _project.ReminderSilent = false;
        RefreshLabels();
        _changed();
    }

    private void RefreshLabels()
    {
        _deadlineButton.Content = DeadlineValue.ButtonLabel(
            _project.Deadline,
            _project.DeadlineMinute,
            _project.NotifyBeforeMinutes,
            _project.DeadlineSilent);
        var past = _project.Deadline is { } date && DeadlineValue.IsPast(date, _project.DeadlineMinute);
        _deadlineButton.Foreground = Brush(past ? AppearanceService.DangerTextBrush : AppearanceService.AccentTextBrush);
        _clearDeadline.IsVisible = _project.Deadline is not null;

        _reminderButton.Content = ReminderText.ButtonLabel(_project.ReminderOn, _project.ReminderEveryMinutes, _project.ReminderSilent);
        _clearReminder.IsVisible = _project.ReminderOn is not null;
    }

    private void ShowNotify(int mode, bool hours = false)
    {
        _notifyNone.Set(mode == 0);
        _notifyOn.Set(mode == 1);
        _notifyBefore.Set(mode == 2);
        _deadlineLeadPanel.IsVisible = mode == 2;
        if (mode == 2)
            ShowLeadUnit(hours);
    }

    private void ShowLeadUnit(bool hours)
    {
        _leadMinutes.Set(!hours);
        _leadHours.Set(hours);
    }

    private void ShowReminderMode(bool repeating)
    {
        _once.Set(!repeating);
        _repeat.Set(repeating);
        _reminderOnce.IsVisible = !repeating;
        _reminderRepeat.IsVisible = repeating;
    }

    private void ShowIntervalUnit(int unit)
    {
        _intervalMinutes.Set(unit == 0);
        _intervalHours.Set(unit == 1);
        _intervalDays.Set(unit == 2);
    }

    private void HideDeadline() => _deadlineFlyout.Hide();

    private void HideReminder() => _reminderFlyout.Hide();

    private static void SplitInterval(int minutes, out int count, out int unit)
    {
        if (minutes >= 1440 && minutes % 1440 == 0)
        {
            count = minutes / 1440;
            unit = 2;
            return;
        }

        if (minutes >= 60 && minutes % 60 == 0)
        {
            count = minutes / 60;
            unit = 1;
            return;
        }

        count = Math.Max(1, minutes);
        unit = 0;
    }

    private static void SetPair(Toggle am, Toggle pm, bool amSelected)
    {
        am.Set(amSelected);
        pm.Set(!amSelected);
    }

    private static void ShowError(TextBlock error, string message)
    {
        error.Text = message;
        error.IsVisible = true;
    }

    private static void EditorKey(KeyEventArgs e, Action cancel, Action commit)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            cancel();
            return;
        }

        if (e.Key != Key.Enter && e.Key != Key.Return)
            return;

        e.Handled = true;
        commit();
    }

    private static Button Link(string label, Flyout flyout) => new()
    {
        Content = label,
        Flyout = flyout,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0),
        MinHeight = 0,
        FontSize = AppearanceService.Font(13),
        FontWeight = FontWeight.SemiBold,
        Foreground = Brush(AppearanceService.AccentTextBrush),
        HorizontalAlignment = HorizontalAlignment.Left,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Cursor = new Cursor(StandardCursorType.Hand)
    };

    private static Button Clear(string tip, Action click)
    {
        var button = new Button
        {
            Content = "×",
            IsVisible = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0, 0, 0),
            MinHeight = 0,
            FontSize = AppearanceService.Font(14),
            Foreground = Brush(AppearanceService.TextMutedBrush),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private static StackPanel Actions(Action cancel, Action commit)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        row.Children.Add(Command("Cancel", primary: false, cancel));
        row.Children.Add(Command("Set", primary: true, commit));
        return row;
    }

    private static Button Command(string label, bool primary, Action click)
    {
        var button = new Button
        {
            Content = label,
            Height = 32,
            MinWidth = 72,
            Padding = new Thickness(14, 0),
            CornerRadius = new CornerRadius(8),
            FontSize = AppearanceService.Font(13),
            FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        if (primary)
        {
            button.Background = Brush(AppearanceService.AccentBrush);
            button.Foreground = Brush(AppearanceService.TextOnAccentBrush);
            button.BorderThickness = new Thickness(0);
        }
        else
        {
            button.Background = Brushes.Transparent;
            button.Foreground = Brush(AppearanceService.TextSecondaryBrush);
            button.BorderBrush = Brush(AppearanceService.BorderBrush);
            button.BorderThickness = new Thickness(1);
        }

        button.Click += (_, _) => click();
        return button;
    }

    private static TextBox TimeBox(string placeholder, double width = 56) => new()
    {
        PlaceholderText = placeholder,
        Width = width,
        Height = 36,
        Padding = new Thickness(10, 8),
        MaxLength = 3,
        FontSize = AppearanceService.Font(14),
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = Brush(AppearanceService.SurfaceInsetBrush),
        BorderBrush = Brush(AppearanceService.BorderBrush),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8)
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = AppearanceService.Font(11),
        FontWeight = FontWeight.SemiBold,
        Foreground = Brush(AppearanceService.TextMutedBrush)
    };

    private static TextBlock Colon() => new()
    {
        Text = ":",
        FontSize = AppearanceService.Font(16),
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brush(AppearanceService.TextMutedBrush)
    };

    private static TextBlock Error() => new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = AppearanceService.Font(12),
        Foreground = Brush(AppearanceService.DangerTextBrush),
        IsVisible = false
    };

    private static IBrush Brush(string key) => AppearanceService.Brush(key);

    private sealed class Toggle
    {
        public Button Button { get; }

        public bool Selected { get; private set; }

        public Toggle(string label)
        {
            Button = new Button
            {
                Content = label,
                Height = 32,
                MinWidth = 72,
                Padding = new Thickness(8, 0),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                FontSize = AppearanceService.Font(13),
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            Set(false);
        }

        public void OnClick(Action click) => Button.Click += (_, _) => click();

        public void Set(bool selected)
        {
            Selected = selected;
            Button.Background = Brush(selected ? AppearanceService.AccentPressedBrush : AppearanceService.SurfaceInsetBrush);
            Button.BorderBrush = Brush(selected ? AppearanceService.AccentPressedBrush : AppearanceService.BorderBrush);
            Button.Foreground = selected
                ? Brush(AppearanceService.TextOnAccentBrush)
                : Brush(AppearanceService.TextBodyBrush);
        }
    }
}
