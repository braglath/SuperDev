using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SuperDev.Models;

namespace SuperDev.Views;

public partial class AddCheckWindow : Window
{
   private readonly ChecklistItem? _editingItem;
   private double _compactHeight;
   private bool _notesExpanded;
   private DateOnly? _deadline;
   private int? _deadlineMinute;
   private int? _notifyBeforeMinutes;
   private bool _deadlineSilent;
   private readonly ObservableCollection<ChecklistSubtask> _links = [];
   private readonly ObservableCollection<ChecklistSubtask> _subtasks = [];
   private readonly HashSet<string> _takenTitles = new(StringComparer.OrdinalIgnoreCase);

   public Func<Window, Task<ChecklistSubtask?>>? RequestLink { get; set; }

   public AddCheckWindow(IEnumerable<string>? takenTitles = null)
   {
      RememberTitles(takenTitles);
      InitializeComponent();

      Title = "Add Check";

      CategoryComboBox.SelectedIndex = 0;
      PriorityComboBox.SelectedIndex = 1;
      ApplyPriorityColor();
      LinkList.ItemsSource = _links;
      SubtaskList.ItemsSource = _subtasks;
   }

   protected override void OnOpened(EventArgs e)
   {
      base.OnOpened(e);

      if (_editingItem is null)
         TitleTextBox.Focus();
   }

   public AddCheckWindow(ChecklistItem item, IEnumerable<string>? takenTitles = null)
   {
      RememberTitles(takenTitles);
      InitializeComponent();

      Title = "Edit Check";

      _editingItem = item;

      TitleTextBox.Text = item.Title;

      SelectComboBoxItem(
          CategoryComboBox,
          item.Category);

      PriorityComboBox.SelectedIndex =
          (int)item.Priority;
      ApplyPriorityColor();

      NotesTextBox.Text = item.Notes;
      _deadline = item.Deadline;
      _deadlineMinute = item.Deadline is null ? null : item.DeadlineMinute;
      _notifyBeforeMinutes = item.Deadline is null ? null : item.NotifyBeforeMinutes;
      _deadlineSilent = item.Deadline is not null && item.DeadlineSilent;
      foreach (var subtask in item.Subtasks ?? [])
      {
         var copy = new ChecklistSubtask
         {
            Id = subtask.Id,
            Title = subtask.Title,
            IsCompleted = subtask.IsCompleted,
            LinkedDocumentId = subtask.LinkedDocumentId,
            LinkedFilePath = subtask.LinkedFilePath
         };
         if (copy.IsLink)
            _links.Add(copy);
         else
            _subtasks.Add(copy);
      }

      LinkList.ItemsSource = _links;
      SubtaskList.ItemsSource = _subtasks;
      ApplyDeadlineLink();
   }

   private void AddSubtask_Click(object? sender, RoutedEventArgs e) =>
       AddSubtask();

   private void SubtaskTextBox_KeyDown(object? sender, KeyEventArgs e)
   {
      if (e.Key != Key.Enter && e.Key != Key.Return)
         return;

      AddSubtask();
      e.Handled = true;
   }

   private void AddSubtask()
   {
      var title = SubtaskTextBox.Text?.Trim();
      if (string.IsNullOrWhiteSpace(title))
         return;

      _subtasks.Add(new ChecklistSubtask { Title = title });
      SubtaskTextBox.Text = string.Empty;
      SubtaskTextBox.Focus();
      Dispatcher.UIThread.Post(FormScroll.ScrollToEnd, DispatcherPriority.Loaded);
   }

   private void RemoveLink_Click(object? sender, RoutedEventArgs e)
   {
      if (sender is Button { DataContext: ChecklistSubtask subtask })
         _links.Remove(subtask);
   }

   private void RemoveSubtask_Click(object? sender, RoutedEventArgs e)
   {
      if (sender is Button { DataContext: ChecklistSubtask subtask })
         _subtasks.Remove(subtask);
   }

   private async void LinkChecklist_Click(object? sender, RoutedEventArgs e)
   {
      if (RequestLink is null)
         return;

      var subtask = await RequestLink(this);
      if (subtask is null)
         return;

      var duplicate = _links.Any(existing =>
          existing.LinkedDocumentId is Guid id &&
          id == subtask.LinkedDocumentId);
      if (duplicate)
         return;

      _links.Add(subtask);
      Dispatcher.UIThread.Post(FormScroll.ScrollToEnd, DispatcherPriority.Loaded);
   }

   private void PriorityComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
       ApplyPriorityColor();

   private void ApplyPriorityColor()
   {
      if (PriorityComboBox.SelectedIndex < 0)
         return;

      PriorityComboBox.Foreground =
          PriorityAppearance.BrushFor((ChecklistPriority)PriorityComboBox.SelectedIndex);
   }

   private void DeadlineFlyout_Opened(object? sender, EventArgs e)
   {
      DeadlineError.IsVisible = false;
      var selected = _deadline is { } date ? DeadlineValue.ToCalendar(date) : DateTime.Today;
      DeadlineCalendar.SelectedDate = _deadline is null ? null : selected;
      DeadlineCalendar.DisplayDate = selected;

      var minute = _deadlineMinute ?? (9 * 60);
      DeadlineValue.SplitClock(minute, out var hour, out var clockMinute, out var pm);
      DeadlineHourBox.Text = hour.ToString(CultureInfo.InvariantCulture);
      DeadlineMinuteBox.Text = clockMinute.ToString("00", CultureInfo.InvariantCulture);
      SetDeadlineMeridiem(pm);

      if (_notifyBeforeMinutes is int lead && lead > 0)
      {
         DeadlineValue.SplitLead(lead, out var count, out var unit);
         DeadlineLeadBox.Text = count.ToString(CultureInfo.InvariantCulture);
         ShowDeadlineNotify(2, unit == 1);
      }
      else if (_notifyBeforeMinutes == 0)
      {
         DeadlineLeadBox.Text = "30";
         ShowDeadlineNotify(1, hours: false);
      }
      else
      {
         DeadlineLeadBox.Text = "30";
         ShowDeadlineNotify(_deadline is null ? 1 : 0, hours: false);
      }

      SetChoice(DeadlineSilentButton, _deadlineSilent);
   }

   private void DeadlineSilent_Click(object? sender, RoutedEventArgs e) =>
       SetChoice(DeadlineSilentButton, !DeadlineSilentButton.Classes.Contains("on"));

   private void DeadlineMeridiem_Click(object? sender, RoutedEventArgs e) =>
       SetDeadlineMeridiem(sender == DeadlinePmButton);

   private void DeadlineNotify_Click(object? sender, RoutedEventArgs e)
   {
      var mode = sender == DeadlineNotifyBeforeButton ? 2 : sender == DeadlineNotifyOnButton ? 1 : 0;
      ShowDeadlineNotify(mode, DeadlineLeadHoursButton.Classes.Contains("on"));
   }

   private void DeadlineLeadUnit_Click(object? sender, RoutedEventArgs e) =>
       ShowDeadlineNotify(2, sender == DeadlineLeadHoursButton);

   private void DeadlineCancel_Click(object? sender, RoutedEventArgs e) =>
       DeadlineButton.Flyout?.Hide();

   private void DeadlineSet_Click(object? sender, RoutedEventArgs e) =>
       CommitDeadlineFromEditor();

   private void DeadlineEditor_KeyDown(object? sender, KeyEventArgs e)
   {
      if (e.Key == Key.Escape)
      {
         e.Handled = true;
         DeadlineButton.Flyout?.Hide();
         return;
      }

      if (e.Key != Key.Enter && e.Key != Key.Return)
         return;

      e.Handled = true;
      CommitDeadlineFromEditor();
   }

   private void ShowDeadlineNotify(int mode, bool hours)
   {
      DeadlineLeadPanel.IsVisible = mode == 2;
      SetChoice(DeadlineNotifyNoneButton, mode == 0);
      SetChoice(DeadlineNotifyOnButton, mode == 1);
      SetChoice(DeadlineNotifyBeforeButton, mode == 2);
      SetChoice(DeadlineLeadMinutesButton, !hours);
      SetChoice(DeadlineLeadHoursButton, hours);
   }

   private void CommitDeadlineFromEditor()
   {
      if (DeadlineCalendar.SelectedDate is not { } selected)
      {
         DeadlineError.Text = "Pick a date.";
         DeadlineError.IsVisible = true;
         return;
      }

      if (!DeadlineValue.TryClock(
              DeadlineHourBox.Text,
              DeadlineMinuteBox.Text,
              DeadlinePmButton.Classes.Contains("on"),
              out var minute,
              out var hourLabel,
              out var pm,
              out var error))
      {
         DeadlineError.Text = error;
         DeadlineError.IsVisible = true;
         return;
      }

      int? lead = null;
      if (DeadlineNotifyBeforeButton.Classes.Contains("on"))
      {
         if (!DeadlineValue.TryLead(DeadlineLeadBox.Text, DeadlineLeadHoursButton.Classes.Contains("on") ? 1 : 0, out var before, out error))
         {
            DeadlineError.Text = error;
            DeadlineError.IsVisible = true;
            return;
         }

         lead = before;
      }
      else if (DeadlineNotifyOnButton.Classes.Contains("on"))
      {
         lead = 0;
      }

      DeadlineHourBox.Text = hourLabel;
      DeadlineMinuteBox.Text = (minute % 60).ToString("00", CultureInfo.InvariantCulture);
      SetDeadlineMeridiem(pm);
      DeadlineError.IsVisible = false;
      DeadlineButton.Flyout?.Hide();

      _deadline = DeadlineValue.FromCalendar(selected);
      _deadlineMinute = minute;
      _notifyBeforeMinutes = lead;
      _deadlineSilent = DeadlineSilentButton.Classes.Contains("on");
      ApplyDeadlineLink();
   }

   private void SetDeadlineMeridiem(bool pm)
   {
      SetChoice(DeadlinePmButton, pm);
      SetChoice(DeadlineAmButton, !pm);
   }

   private static void SetChoice(Button button, bool selected)
   {
      if (selected)
         button.Classes.Add("on");
      else
         button.Classes.Remove("on");
   }

   private void ClearDeadline_Click(object? sender, RoutedEventArgs e)
   {
      _deadline = null;
      _deadlineMinute = null;
      _notifyBeforeMinutes = null;
      _deadlineSilent = false;
      ApplyDeadlineLink();
   }

   private void ApplyDeadlineLink()
   {
      DeadlineButton.Content = DeadlineValue.ButtonLabel(_deadline, _deadlineMinute, _notifyBeforeMinutes, _deadlineSilent);
      ClearDeadlineButton.IsVisible = _deadline is not null;
      var late = _deadline is { } date && DeadlineValue.IsPast(date, _deadlineMinute);
      if (late)
         DeadlineButton.Classes.Add("late");
      else
         DeadlineButton.Classes.Remove("late");
   }

   private void SelectComboBoxItem(
       ComboBox comboBox,
       string value)
   {
      foreach (var item in comboBox.Items)
      {
         if (item is ComboBoxItem comboItem &&
             comboItem.Content?.ToString() == value)
         {
            comboBox.SelectedItem = comboItem;
            return;
         }
      }
   }

   private void ToggleNotes_Click(object? sender, RoutedEventArgs e)
   {
      if (_notesExpanded)
      {
         SetHeightWithinScreen(_compactHeight);
         ExpandNotesButton.Content = "Expand";
         _notesExpanded = false;
         return;
      }

      _compactHeight = Height;
      SetHeightWithinScreen(Math.Max(Height + 280, 760));
      ExpandNotesButton.Content = "Collapse";
      _notesExpanded = true;
      NotesTextBox.Focus();
   }

   private void SetHeightWithinScreen(double height)
   {
      var screen = Screens?.ScreenFromWindow(this);
      if (screen is null || screen.Scaling <= 0)
      {
         Height = height;
         return;
      }

      var scale = screen.Scaling;
      var work = screen.WorkingArea;
      var chrome = FrameChromeDip();
      var maxHeight = Math.Max(MinHeight, work.Height / scale - chrome);
      Height = Math.Clamp(height, MinHeight, maxHeight);

      var frameHeightPx = (int)Math.Ceiling((Height + chrome) * scale);
      var top = Position.Y;
      var bottomLimit = work.Y + work.Height;
      if (top + frameHeightPx > bottomLimit)
         top = bottomLimit - frameHeightPx;
      if (top < work.Y)
         top = work.Y;

      if (top != Position.Y)
         Position = new PixelPoint(Position.X, top);
   }

   private double FrameChromeDip()
   {
      if (FrameSize is { } frame && ClientSize.Height > 0)
         return Math.Max(0, frame.Height - ClientSize.Height) + 12;

      return 56;
   }

   private void TitleTextBox_TextChanged(object? sender, TextChangedEventArgs e) =>
       TitleError.IsVisible = false;

   private void RememberTitles(IEnumerable<string>? takenTitles)
   {
      if (takenTitles is null)
         return;

      foreach (var title in takenTitles)
      {
         var trimmed = title.Trim();
         if (trimmed.Length > 0)
            _takenTitles.Add(trimmed);
      }
   }

   private void Cancel_Click(
       object? sender,
       RoutedEventArgs e)
   {
      Close(null);
   }

   private void Save_Click(
       object? sender,
       RoutedEventArgs e)
   {
      var title = TitleTextBox.Text?.Trim();

      if (string.IsNullOrWhiteSpace(title))
         return;

      if (_takenTitles.Contains(title))
      {
         TitleError.IsVisible = true;
         TitleTextBox.Focus();
         return;
      }

      var category =
          (CategoryComboBox.SelectedItem as ComboBoxItem)
              ?.Content?.ToString()
          ?? "Development";

      var priority =
          (ChecklistPriority)PriorityComboBox.SelectedIndex;

      var notes = NotesTextBox.Text?.Trim() ?? string.Empty;

      var subtasks = _links
          .Concat(_subtasks)
          .Where(subtask => !string.IsNullOrWhiteSpace(subtask.Title))
          .Select(subtask => new ChecklistSubtask
          {
             Id = subtask.Id,
             Title = subtask.Title.Trim(),
             IsCompleted = subtask.IsCompleted,
             LinkedDocumentId = subtask.LinkedDocumentId,
             LinkedFilePath = subtask.LinkedFilePath
          })
          .ToList();
      var completed = _editingItem?.IsCompleted ?? false;
      if (subtasks.Any(subtask => !subtask.IsCompleted))
         completed = false;

      var item = new ChecklistItem
      {
         Id = _editingItem?.Id ?? Guid.NewGuid(),
         Title = title,
         Category = category,
         Priority = priority,
         Notes = notes,
         Deadline = _deadline,
         DeadlineMinute = _deadline is null ? null : _deadlineMinute,
         NotifyBeforeMinutes = _deadline is null ? null : _notifyBeforeMinutes,
         DeadlineSilent = _deadline is not null && _deadlineSilent,
         Subtasks = subtasks,
         IsCompleted = completed
      };

      Close(item);
   }
}