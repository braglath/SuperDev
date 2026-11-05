
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SuperDev.Models;
using SuperDev.Services;
using SuperDev.Services.Ai;

namespace SuperDev.Views;

public partial class ChecklistView : UserControl
{
   private enum StatusFilter
   {
      All,
      Open,
      Completed,
      Skipped
   }

   private enum PriorityFilter
   {
      All,
      Critical,
      Required,
      Conditional,
      Review
   }

   private enum ChecklistSort
   {
      Order,
      Priority,
      Category,
      Title
   }

   private readonly WorkType _workType;
   private readonly string? _templateName;
   private readonly ObservableCollection<ChecklistItem> _checklist;
   private readonly ObservableCollection<ChecklistItem> _visible = new();
   private bool _viewReady;
   private bool _filtersVisible = true;
   private bool _syncingCheck;
   private bool _editOpen;
   private bool _linksOpen;
   private bool _descriptionOpen;
   private bool _documentCompleted;
   private bool _completionOfferOpen;
   private ChecklistItem? _selectedItem;
   private ChecklistItem? _detailItem;
   private CheckDetailWindow? _detailPopup;
   private ChecklistReviewWindow? _reviewWindow;
   private ChecklistReviewResult? _reviewResult;
   private TaskExplainWindow? _explainWindow;
   private bool _explainOpening;
   private Window? _hostWindow;
   private bool _detailOnSide;
   private bool _switchingDetail;
   private readonly HashSet<Key> _itemKeysDown = new();
   private TopLevel? _inputRoot;
   private Border? _subtaskDragRow;
   private StackPanel? _subtaskDragHost;
   private ChecklistItem? _subtaskDragItem;
   private IPointer? _subtaskPointer;
   private Point _subtaskDragStart;
   private bool _subtaskDragging;
   private int _subtaskPendingTarget = -1;
   private bool _subtaskMoveQueued;
   private bool _subtaskReordered;
   private bool _subtaskDeleteOpen;
   public string WorkTitle { get; private set; }

   public string BranchName { get; private set; }

   public string PullRequestUrl { get; private set; }

   public DateOnly? Deadline { get; private set; }

   public int? DeadlineMinute { get; private set; }

   public int? NotifyBeforeMinutes { get; private set; }

   public bool DeadlineSilent { get; private set; }

   public DateTime? ReminderOn { get; private set; }

   public int ReminderEveryMinutes { get; private set; }

   public bool ReminderSilent { get; private set; }

   public WorkType WorkType => _workType;

   public string? TemplateName => _templateName;

   public IReadOnlyList<ChecklistItem> Items => _checklist;

   public event EventHandler? DocumentEdited;

   public event EventHandler? DocumentCompleted;

   public event EventHandler? DocumentReopened;

   public event EventHandler? SaveRequested;

   public event EventHandler? SaveAsTemplateRequested;

   public event EventHandler? CopyRequested;

   public event EventHandler? ExportJsonRequested;

   public event EventHandler? ExportCsvRequested;

   public event EventHandler? CopyTextRequested;

   public event EventHandler<string>? SubtasksRequired;

   public event EventHandler<ChecklistSubtask>? LinkedSubtaskOpenRequested;

   public event EventHandler<string>? CopiedFromOpenRequested;

   public Func<Window, Task<ChecklistSubtask?>>? RequestLink { get; set; }

   public Func<Window, CancellationToken, Task<bool>>? EnsureAiReady { get; set; }

   public Func<CheckGenerationRequest, CancellationToken, Task<AiGenerationResult>>? GenerateOneCheck { get; set; }

   public Func<ChecklistReviewRequest, CancellationToken, Task<ChecklistReviewOutcome>>? ReviewChecklist { get; set; }

   public Func<TaskExplainRequest, CancellationToken, Task<TaskExplainOutcome>>? ExplainTask { get; set; }

   public string? Description { get; private set; }

   public string? TaskDescription { get; private set; }

   public string? AiPlatform { get; private set; }

   public string? AiFramework { get; private set; }

   public void SetDescription(string? description)
   {
      Description = CleanContext(description);
      ApplyDescription();
   }

   public void SetAiContext(string? taskDescription, string? platform, string? framework)
   {
      TaskDescription = CleanContext(taskDescription);
      AiPlatform = CleanContext(platform);
      AiFramework = CleanContext(framework);
   }

   private static string? CleanContext(string? value) =>
       string.IsNullOrWhiteSpace(value) ? null : value.Trim();

   public void SetDocumentCompleted(bool completed)
   {
      _documentCompleted = completed;
      ApplyCompletedChrome();
   }

   public void SetReminder(DateTime? nextUtc, int everyMinutes, bool silent)
   {
      if (everyMinutes < 0)
         everyMinutes = 0;

      var nextSilent = nextUtc is not null && silent;
      if (ReminderOn == nextUtc && ReminderEveryMinutes == everyMinutes && ReminderSilent == nextSilent)
         return;

      ReminderOn = nextUtc;
      ReminderEveryMinutes = everyMinutes;
      ReminderSilent = nextSilent;
      ApplyReminderLink();
      DocumentEdited?.Invoke(this, EventArgs.Empty);
   }

   public void SetDeadlineNotify(int? minutesBefore, bool silent)
   {
      if (Deadline is null)
         return;

      var nextSilent = minutesBefore is not null && silent;
      if (NotifyBeforeMinutes == minutesBefore && DeadlineSilent == nextSilent)
         return;

      NotifyBeforeMinutes = minutesBefore;
      DeadlineSilent = nextSilent;
      UpdateDeadlinePresentation();
      DocumentEdited?.Invoke(this, EventArgs.Empty);
   }

   public void SetItemDeadlineNotify(Guid itemId, int? minutesBefore, bool silent)
   {
      var item = _checklist.FirstOrDefault(entry => entry.Id == itemId);
      if (item?.Deadline is null)
         return;

      item.NotifyBeforeMinutes = minutesBefore;
      item.DeadlineSilent = minutesBefore is not null && silent;
      RefreshDeadlineAppearance();
      DocumentEdited?.Invoke(this, EventArgs.Empty);
   }

   public bool HasIncompleteRequiredChecks() =>
       IncompleteRequiredCount() > 0;

   private int IncompleteRequiredCount() =>
       ChecklistCounts.IncompleteRequired(_checklist);

   public ChecklistView(
       WorkType workType,
       string? templateName,
       string workTitle,
       string branchName,
       string pullRequestUrl,
       DateOnly? deadline,
       int? deadlineMinute,
       int? notifyBeforeMinutes,
       bool deadlineSilent,
       DateTime? reminderOn,
       int reminderEveryMinutes,
       bool reminderSilent,
       List<ChecklistItem> checklist)
   {
      InitializeComponent();
      SizeChanged += (_, _) => SyncDetailLayout();
      SideDetail.CloseRequested += (_, _) => CloseDetails();
      SideDetail.ExplainRequested += (_, _) => ExplainOpenDetail();
      SideDetail.EditRequested += (_, _) => EditOpenDetail();
      SideDetail.DeleteRequested += (_, _) => DeleteOpenDetail();
      SideDetail.CompleteRequested += (_, _) => CompleteOpenDetail();
      SideDetail.SkipRequested += (_, _) => SkipOpenDetail();
      SideDetail.LinkRequested += (_, subtask) =>
          LinkedSubtaskOpenRequested?.Invoke(this, subtask);
      SideDetail.CopiedFromRequested += (_, path) =>
          CopiedFromOpenRequested?.Invoke(this, path);

      _workType = workType;
      _templateName = string.IsNullOrWhiteSpace(templateName) ? null : templateName.Trim();
      WorkTitle = workTitle;
      BranchName = branchName;
      PullRequestUrl = pullRequestUrl;
      Deadline = deadline;
      DeadlineMinute = deadline is null ? null : deadlineMinute;
      NotifyBeforeMinutes = deadline is null ? null : notifyBeforeMinutes;
      DeadlineSilent = deadline is not null && deadlineSilent;
      ReminderOn = reminderOn;
      ReminderEveryMinutes = reminderEveryMinutes < 0 ? 0 : reminderEveryMinutes;
      ReminderSilent = reminderSilent;
      _checklist = new ObservableCollection<ChecklistItem>(checklist);

      WorkflowTitleBox.Text = workTitle;
      ApplyDescription();
      ApplyWorkLinks();
      UpdateDeadlinePresentation();
      ChecklistItems.ItemsSource = _visible;

      StatusFilterBox.SelectedIndex = 0;
      PriorityFilterBox.SelectedIndex = 0;
      SortBox.SelectedIndex = 0;
      _viewReady = true;

      ApplyView();
      UpdateProgress();
      ApplyShortcutTips();
      AddHandler(KeyDownEvent, ItemShortcutKeyDown, RoutingStrategies.Tunnel);
      AddHandler(KeyUpEvent, ItemShortcutKeyUp, RoutingStrategies.Tunnel);
      AddHandler(PointerMovedEvent, SubtaskPointerMoved, RoutingStrategies.Tunnel, true);
      AddHandler(PointerReleasedEvent, SubtaskPointerReleased, RoutingStrategies.Tunnel, true);
   }

   private string FormatTemplateLine(WorkType workType, string? reminder)
   {
      var template = !string.IsNullOrWhiteSpace(_templateName)
          ? _templateName
          : workType switch
          {
             WorkType.Bug => "Bug Fix",
             WorkType.Feature => "Feature",
             WorkType.Development => "Development",
             WorkType.Testing => "Testing",
             WorkType.PullRequest => "Pull Request",
             WorkType.Blank => "Blank Checklist",
             WorkType.Daily => "Daily",
             WorkType.Custom => "Custom",
             _ => string.Empty
          };

      if (string.IsNullOrWhiteSpace(reminder))
         return template;

      return string.IsNullOrWhiteSpace(template)
          ? reminder.Trim()
          : $"{template} · {reminder.Trim()}";
   }

   private void UpdateDeadlinePresentation()
   {
      WorkflowDescription.Text = FormatTemplateLine(_workType, reminder: null);
      ApplyDeadlineLink(DeadlineButton, Deadline);
      ApplyReminderLink();
   }

   private void DeadlineFlyout_Opened(object? sender, EventArgs e)
   {
      DeadlineError.IsVisible = false;
      var selected = Deadline is { } date ? DeadlineValue.ToCalendar(date) : DateTime.Today;
      DeadlineCalendar.SelectedDate = Deadline is null ? null : selected;
      DeadlineCalendar.DisplayDate = selected;

      var minute = DeadlineMinute ?? (9 * 60);
      DeadlineValue.SplitClock(minute, out var hour, out var clockMinute, out var pm);
      DeadlineHourBox.Text = hour.ToString(CultureInfo.InvariantCulture);
      DeadlineMinuteBox.Text = clockMinute.ToString("00", CultureInfo.InvariantCulture);
      SetDeadlineMeridiem(pm);

      if (NotifyBeforeMinutes is int lead && lead > 0)
      {
         DeadlineValue.SplitLead(lead, out var count, out var unit);
         DeadlineLeadBox.Text = count.ToString(CultureInfo.InvariantCulture);
         ShowDeadlineNotify(2, unit == 1);
      }
      else if (NotifyBeforeMinutes == 0)
      {
         DeadlineLeadBox.Text = "30";
         ShowDeadlineNotify(1, hours: false);
      }
      else
      {
         DeadlineLeadBox.Text = "30";
         ShowDeadlineNotify(Deadline is null ? 1 : 0, hours: false);
      }

      SetChoice(DeadlineSilentButton, DeadlineSilent);
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

      var next = DeadlineValue.FromCalendar(selected);
      var silent = DeadlineSilentButton.Classes.Contains("on");
      if (Deadline == next && DeadlineMinute == minute && NotifyBeforeMinutes == lead && DeadlineSilent == silent)
         return;

      Deadline = next;
      DeadlineMinute = minute;
      NotifyBeforeMinutes = lead;
      DeadlineSilent = silent;
      UpdateDeadlinePresentation();
      DocumentEdited?.Invoke(this, EventArgs.Empty);
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

   private void ReminderFlyout_Opened(object? sender, EventArgs e)
   {
      ReminderError.IsVisible = false;
      var local = ReminderOn?.ToLocalTime();
      var selected = local?.Date ?? DateTime.Today;
      ReminderCalendar.SelectedDate = selected;
      ReminderCalendar.DisplayDate = selected;

      var hour = local?.Hour ?? 9;
      ReminderHourBox.Text = (hour % 12 == 0 ? 12 : hour % 12).ToString(CultureInfo.InvariantCulture);
      ReminderMinuteBox.Text = (local?.Minute ?? 0).ToString("00", CultureInfo.InvariantCulture);
      SetMeridiem(hour >= 12);
      SetChoice(ReminderSilentButton, ReminderSilent);

      if (ReminderEveryMinutes > 0)
      {
         SplitInterval(ReminderEveryMinutes, out var count, out var unit);
         ReminderIntervalBox.Text = count.ToString(CultureInfo.InvariantCulture);
         ReminderIntervalUnit.SelectedIndex = unit;
         ShowReminderMode(repeating: true);
      }
      else
      {
         ReminderIntervalBox.Text = "1";
         ReminderIntervalUnit.SelectedIndex = 1;
         ShowReminderMode(repeating: false);
      }
   }

   private void ReminderMeridiem_Click(object? sender, RoutedEventArgs e) =>
      SetMeridiem(sender == ReminderPmButton);

   private void ReminderMode_Click(object? sender, RoutedEventArgs e) =>
      ShowReminderMode(repeating: sender == ReminderRepeatButton);

   private void ReminderSilent_Click(object? sender, RoutedEventArgs e) =>
      SetChoice(ReminderSilentButton, !ReminderSilentButton.Classes.Contains("on"));

   private void ShowReminderMode(bool repeating)
   {
      ReminderOncePanel.IsVisible = !repeating;
      ReminderIntervalPanel.IsVisible = repeating;
      if (repeating)
      {
         ReminderRepeatButton.Classes.Add("on");
         ReminderOnceButton.Classes.Remove("on");
      }
      else
      {
         ReminderOnceButton.Classes.Add("on");
         ReminderRepeatButton.Classes.Remove("on");
      }
   }

   private void ReminderCancel_Click(object? sender, RoutedEventArgs e) =>
      ReminderButton.Flyout?.Hide();

   private void ReminderSet_Click(object? sender, RoutedEventArgs e) =>
      CommitReminderFromEditor();

   private void ReminderEditor_KeyDown(object? sender, KeyEventArgs e)
   {
      if (e.Key == Key.Escape)
      {
         e.Handled = true;
         ReminderButton.Flyout?.Hide();
         return;
      }

      if (e.Key != Key.Enter && e.Key != Key.Return)
         return;

      e.Handled = true;
      CommitReminderFromEditor();
   }

   private void ClearReminder_Click(object? sender, RoutedEventArgs e)
   {
      if (ReminderOn is null && ReminderEveryMinutes == 0 && !ReminderSilent)
         return;

      ReminderOn = null;
      ReminderEveryMinutes = 0;
      ReminderSilent = false;
      ApplyReminderLink();
      DocumentEdited?.Invoke(this, EventArgs.Empty);
   }

   public bool ApplyExternalReminder(DateTime? nextUtc, int everyMinutes)
   {
      if (everyMinutes < 0)
         everyMinutes = 0;

      if (ReminderOn == nextUtc && ReminderEveryMinutes == everyMinutes)
         return false;

      ReminderOn = nextUtc;
      ReminderEveryMinutes = everyMinutes;
      ApplyReminderLink();
      return true;
   }

   private void CommitReminderFromEditor()
   {
      if (!TryReadReminder(out var next, out var everyMinutes, out var error))
      {
         ReminderError.Text = error;
         ReminderError.IsVisible = true;
         return;
      }

      ReminderError.IsVisible = false;
      ReminderButton.Flyout?.Hide();

      var silent = ReminderSilentButton.Classes.Contains("on");
      if (ReminderOn == next && ReminderEveryMinutes == everyMinutes && ReminderSilent == silent)
         return;

      ReminderOn = next;
      ReminderEveryMinutes = everyMinutes;
      ReminderSilent = silent;
      ApplyReminderLink();
      DocumentEdited?.Invoke(this, EventArgs.Empty);
   }

   private bool TryReadReminder(out DateTime nextUtc, out int everyMinutes, out string error)
   {
      nextUtc = default;
      everyMinutes = 0;
      error = string.Empty;

      if (ReminderIntervalPanel.IsVisible)
      {
         if (!int.TryParse(ReminderIntervalBox.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
             count is < 1 or > 999)
         {
            error = "Enter a repeat interval from 1 to 999.";
            return false;
         }

         everyMinutes = ReminderIntervalUnit.SelectedIndex switch
         {
            2 => count * 1440,
            1 => count * 60,
            _ => count
         };

         if (ReminderEveryMinutes == everyMinutes && ReminderOn is { } existing && existing > DateTime.UtcNow)
         {
            nextUtc = existing;
            return true;
         }

         var localNow = DateTime.Now;
         var start = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, localNow.Minute, 0, DateTimeKind.Local);
         nextUtc = start.AddMinutes(everyMinutes).ToUniversalTime();
         return true;
      }

      if (!TryReadClock(out var time))
      {
         error = "Enter an hour from 1 to 12, or 00 to 23, and minutes from 00 to 59.";
         return false;
      }

      var day = ReminderCalendar.SelectedDate?.Date ?? DateTime.Today;
      var local = new DateTime(day.Year, day.Month, day.Day, time.Hour, time.Minute, 0, DateTimeKind.Local);
      nextUtc = local.ToUniversalTime();
      return true;
   }

   private bool TryReadClock(out TimeOnly time)
   {
      time = default;
      if (!int.TryParse(ReminderHourBox.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var hour))
         return false;

      if (!int.TryParse(ReminderMinuteBox.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minute) ||
          minute is < 0 or > 59)
         return false;

      if (hour is >= 0 and <= 23 && (hour > 12 || hour == 0))
      {
         time = new TimeOnly(hour, minute);
         SetMeridiem(hour >= 12);
         ReminderHourBox.Text = (hour % 12 == 0 ? 12 : hour % 12).ToString(CultureInfo.InvariantCulture);
         return true;
      }

      if (hour is < 1 or > 12)
         return false;

      var hour24 = hour % 12;
      if (ReminderPmButton.Classes.Contains("on"))
         hour24 += 12;

      time = new TimeOnly(hour24, minute);
      return true;
   }

   private void SetMeridiem(bool pm)
   {
      if (pm)
      {
         ReminderPmButton.Classes.Add("on");
         ReminderAmButton.Classes.Remove("on");
      }
      else
      {
         ReminderAmButton.Classes.Add("on");
         ReminderPmButton.Classes.Remove("on");
      }
   }

   private static void SplitInterval(int minutes, out int count, out int unitIndex)
   {
      if (minutes >= 1440 && minutes % 1440 == 0)
      {
         count = minutes / 1440;
         unitIndex = 2;
         return;
      }

      if (minutes >= 60 && minutes % 60 == 0)
      {
         count = minutes / 60;
         unitIndex = 1;
         return;
      }

      count = Math.Max(1, minutes);
      unitIndex = 0;
   }

   private void ApplyReminderLink()
   {
      ReminderButton.Content = ReminderText.ButtonLabel(ReminderOn, ReminderEveryMinutes, ReminderSilent);
      ClearReminderButton.IsVisible = ReminderOn is not null;
   }

   private void ClearDeadline_Click(object? sender, RoutedEventArgs e)
   {
      if (Deadline is null)
         return;

      Deadline = null;
      DeadlineMinute = null;
      NotifyBeforeMinutes = null;
      DeadlineSilent = false;
      UpdateDeadlinePresentation();
      DocumentEdited?.Invoke(this, EventArgs.Empty);
   }

   private void ApplyDeadlineLink(Button link, DateOnly? deadline)
   {
      link.Content = DeadlineValue.ButtonLabel(deadline, DeadlineMinute, NotifyBeforeMinutes, DeadlineSilent);
      SetLate(link, deadline is { } date && DeadlineValue.IsPast(date, DeadlineMinute));

      if (link.Parent is not Panel panel)
         return;

      var clear = panel.Children.OfType<Button>().FirstOrDefault(button => button != link);
      if (clear is not null)
         clear.IsVisible = deadline is not null;
   }

   private void UpdateProgress()
   {
      var progress = ChecklistCounts.Measure(_checklist);
      ProgressText.Text = progress.Label;
      ProgressBar.Value = progress.Percent;
   }

   private Window? GetOwnerWindow() =>
       TopLevel.GetTopLevel(this) as Window;

   private Window? DialogOwner() =>
       _detailPopup is { IsVisible: true } ? _detailPopup : GetOwnerWindow();

   private void CheckBox_Click(object? sender, RoutedEventArgs e)
   {
      if (_syncingCheck)
         return;

      if (sender is not CheckBox checkBox ||
          checkBox.DataContext is not ChecklistItem item)
         return;

      var completed = checkBox.IsChecked == true;
      if (completed && !item.CanComplete)
      {
         SetCheckBox(checkBox, false);
         item.IsCompleted = false;
         SelectItem(item);
         NotifySubtasksRequired(item);
         return;
      }

      item.MarkCompleted(completed);
      SelectItem(item);
      if (FindItemBorder(item) is { } border)
         ApplyItemState(border);

      if (item.IsCompleted)
         SuperDev.SuccessSound.PlayCheck();

      MarkEdited();
      if (item.IsCompleted)
         _ = OfferChecklistCompletionAsync();
   }

   private void SubtaskCheck_Click(object? sender, RoutedEventArgs e)
   {
      if (_syncingCheck)
         return;

      if (sender is not CheckBox checkBox ||
          checkBox.DataContext is not ChecklistSubtask subtask ||
          subtask.IsLink ||
          FindOwnerItem(checkBox) is not ChecklistItem item)
         return;

      subtask.IsCompleted = checkBox.IsChecked == true;
      if (!item.CanComplete)
         item.IsCompleted = false;

      SyncParentCheckBox(item);
      SelectItem(item);

      if (subtask.IsCompleted)
         SuperDev.SuccessSound.PlayCheck();

      MarkEdited();
   }

   private void ManualSubtasks_Loaded(object? sender, RoutedEventArgs e)
   {
      if (sender is not StackPanel host || FindOwnerItem(host) is not ChecklistItem item)
         return;

      host.Children.Clear();
      foreach (var subtask in item.Subtasks)
      {
         if (!subtask.IsManual)
            continue;

         host.Children.Add(CreateManualSubtaskRow(subtask));
      }

      host.IsVisible = host.Children.Count > 0;
   }

   private Border CreateManualSubtaskRow(ChecklistSubtask subtask)
   {
      var title = new TextBox
      {
         Classes = { "subtask-inline" },
         Text = subtask.Title,
         PlaceholderText = "Subtask",
         DataContext = subtask
      };
      ToolTip.SetTip(title, "Edit subtask title");

      var check = new CheckBox
      {
         IsChecked = subtask.IsCompleted,
         VerticalAlignment = VerticalAlignment.Center,
         DataContext = subtask
      };
      check.Click += SubtaskCheck_Click;

      var remove = new Button
      {
         Classes = { "subtask-remove" },
         Content = "×",
         DataContext = subtask
      };
      ToolTip.SetTip(remove, "Delete subtask");
      remove.Click += DeleteSubtask_Click;

      var grip = new Border
      {
         Classes = { "subtask-grip" },
         Child = new TextBlock
         {
            Text = "⋮⋮",
            Classes = { "subtask-grip-mark" }
         }
      };
      ToolTip.SetTip(grip, "Drag to reorder");
      grip.PointerPressed += SubtaskGrip_PointerPressed;

      var fields = new Grid
      {
         ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto,*,Auto"),
         ColumnSpacing = 8,
         VerticalAlignment = VerticalAlignment.Center
      };
      Grid.SetColumn(check, 1);
      Grid.SetColumn(title, 2);
      Grid.SetColumn(remove, 3);
      fields.Children.Add(grip);
      fields.Children.Add(check);
      fields.Children.Add(title);
      fields.Children.Add(remove);

      var row = new Border
      {
         Classes = { "subtask-row" },
         DataContext = subtask,
         Margin = new Thickness(0, 0, 0, 2),
         Child = fields
      };

      title.LostFocus += SubtaskTitle_LostFocus;
      title.KeyDown += SubtaskTitle_KeyDown;
      return row;
   }

   private void SubtaskTitle_LostFocus(object? sender, RoutedEventArgs e)
   {
      if (sender is TextBox box && box.DataContext is ChecklistSubtask subtask)
         CommitSubtaskTitle(box, subtask);
   }

   private void SubtaskTitle_KeyDown(object? sender, KeyEventArgs e)
   {
      if (sender is not TextBox box || box.DataContext is not ChecklistSubtask subtask)
         return;

      if (e.Key == Key.Escape)
      {
         box.Text = subtask.Title;
         e.Handled = true;
         if (FindOwnerItem(box) is { } owner && FindItemBorder(owner) is { } border)
            border.Focus();
         return;
      }

      if (e.Key != Key.Enter && e.Key != Key.Return)
         return;

      e.Handled = true;
      CommitSubtaskTitle(box, subtask);
      if (FindOwnerItem(box) is { } item && FindItemBorder(item) is { } row)
         row.Focus();
   }

   private void CommitSubtaskTitle(TextBox box, ChecklistSubtask subtask)
   {
      if (_documentCompleted)
      {
         if (box.Text != subtask.Title)
            box.Text = subtask.Title;
         return;
      }

      var text = box.Text?.Trim() ?? string.Empty;
      if (text.Length == 0)
      {
         if (box.Text != subtask.Title)
            box.Text = subtask.Title;
         return;
      }

      if (box.Text != text)
         box.Text = text;

      if (subtask.Title == text)
         return;

      subtask.Title = text;
      MarkEdited();
   }

   private async void DeleteSubtask_Click(object? sender, RoutedEventArgs e)
   {
      if (_documentCompleted || _subtaskDeleteOpen)
         return;

      if (sender is not Button button || button.DataContext is not ChecklistSubtask subtask)
         return;

      Border? row = null;
      for (Visual? current = button; current is not null; current = current.GetVisualParent())
      {
         if (current is Border border && border.Classes.Contains("subtask-row"))
         {
            row = border;
            break;
         }
      }

      if (row is null || FindOwnerItem(row) is not ChecklistItem item)
         return;

      var title = subtask.Title.Trim();
      var message = string.IsNullOrWhiteSpace(title)
          ? "Are you sure you want to delete this subtask?"
          : $"Are you sure you want to delete \"{title}\"?";
      var owner = DialogOwner();
      if (owner is null)
         return;

      _subtaskDeleteOpen = true;
      bool confirmed;
      try
      {
         var dialog = new StyledDialog(
             "Delete subtask?",
             message,
             "Delete",
             "Cancel",
             danger: true);
         confirmed = await dialog.ShowDialog<bool>(owner);
      }
      finally
      {
         _subtaskDeleteOpen = false;
      }

      if (!confirmed || !item.Subtasks.Contains(subtask))
         return;

      if (ReferenceEquals(_subtaskDragRow, row))
      {
         var pointer = _subtaskPointer;
         _subtaskPendingTarget = -1;
         ClearSubtaskDrag();
         if (pointer?.Captured == this)
            pointer.Capture(null);
      }

      item.Subtasks.Remove(subtask);
      if (!item.CanComplete)
         item.IsCompleted = false;

      if (row.Parent is StackPanel host)
      {
         host.Children.Remove(row);
         host.IsVisible = host.Children.Count > 0;
      }

      SyncParentCheckBox(item);
      SelectItem(item);
      MarkEdited();
   }

   private void SubtaskGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
   {
      if (_documentCompleted || sender is not Border grip)
         return;

      Border? row = null;
      for (Visual? current = grip; current is not null; current = current.GetVisualParent())
      {
         if (current is Border border && border.Classes.Contains("subtask-row"))
         {
            row = border;
            break;
         }
      }

      if (row?.Parent is not StackPanel host || FindOwnerItem(row) is not ChecklistItem item)
         return;

      if (_subtaskDragRow is not null)
         return;

      if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
         return;

      // The row is removed from the list as it moves. Capturing it would end the gesture after one step.
      _subtaskDragRow = row;
      _subtaskDragHost = host;
      _subtaskDragItem = item;
      _subtaskPointer = e.Pointer;
      _subtaskDragStart = e.GetPosition(host);
      _subtaskDragging = false;
      _subtaskPendingTarget = -1;
      _subtaskReordered = false;
      e.Pointer.Capture(this);
      e.Handled = true;
      SelectItem(item);
   }

   private void SubtaskPointerMoved(object? sender, PointerEventArgs e)
   {
      if (_subtaskDragRow is not { } row || _subtaskDragHost is not { } host || _subtaskPointer != e.Pointer)
         return;

      if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
      {
         EndSubtaskDrag(e);
         return;
      }

      var position = e.GetPosition(host);
      if (!_subtaskDragging)
      {
         var delta = position - _subtaskDragStart;
         if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4)
            return;

         _subtaskDragging = true;
         row.Classes.Add("dragging");
         row.ZIndex = 2;
      }

      ScrollSubtasksWhileDragging(e.GetPosition(ChecklistScroll));
      QueueSubtaskMove(SubtaskIndexAt(host, position.Y, row));
      e.Handled = true;
   }

   private void SubtaskPointerReleased(object? sender, PointerReleasedEventArgs e)
   {
      if (_subtaskDragRow is null || _subtaskPointer != e.Pointer)
         return;

      EndSubtaskDrag(e);
      e.Handled = true;
   }

   protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
   {
      base.OnPointerCaptureLost(e);
      if (_subtaskDragRow is null)
         return;

      CommitPendingSubtaskMove();
      var changed = _subtaskReordered;
      ClearSubtaskDrag();
      if (changed)
         MarkEdited();
   }

   private void EndSubtaskDrag(PointerEventArgs e)
   {
      CommitPendingSubtaskMove();
      var changed = _subtaskReordered;
      ClearSubtaskDrag();
      if (e.Pointer.Captured == this)
         e.Pointer.Capture(null);
      if (changed)
         MarkEdited();
   }

   private void ClearSubtaskDrag()
   {
      if (_subtaskDragRow is { } row)
      {
         row.Classes.Remove("dragging");
         row.ZIndex = 0;
      }

      _subtaskDragRow = null;
      _subtaskDragHost = null;
      _subtaskDragItem = null;
      _subtaskPointer = null;
      _subtaskDragging = false;
      _subtaskPendingTarget = -1;
      _subtaskReordered = false;
   }

   private void QueueSubtaskMove(int target)
   {
      _subtaskPendingTarget = target;
      if (_subtaskMoveQueued)
         return;

      _subtaskMoveQueued = true;
      Dispatcher.UIThread.Post(() =>
      {
         _subtaskMoveQueued = false;
         CommitPendingSubtaskMove();
      }, DispatcherPriority.Input);
   }

   private void CommitPendingSubtaskMove()
   {
      if (_subtaskDragRow is not Border row)
         return;

      var target = _subtaskPendingTarget;
      _subtaskPendingTarget = -1;
      if (target >= 0)
         MoveSubtaskRow(row, target);
   }

   private void ScrollSubtasksWhileDragging(Point pointer)
   {
      var height = ChecklistScroll.Bounds.Height;
      if (height <= 0)
         return;

      var offset = ChecklistScroll.Offset.Y;
      if (pointer.Y < 28)
         offset -= 16;
      else if (pointer.Y > height - 28)
         offset += 16;
      else
         return;

      var max = ChecklistScroll.Extent.Height - height;
      if (max < 0)
         max = 0;
      if (offset < 0)
         offset = 0;
      if (offset > max)
         offset = max;

      ChecklistScroll.Offset = new Vector(0, offset);
   }

   private static int SubtaskIndexAt(StackPanel host, double y, Border dragging)
   {
      var index = 0;
      foreach (var child in host.Children)
      {
         if (ReferenceEquals(child, dragging))
            continue;

         var bounds = child.Bounds;
         if (y > bounds.Y + bounds.Height / 2)
            index++;
      }

      return index;
   }

   private void MoveSubtaskRow(Border row, int target)
   {
      if (_subtaskDragHost is not StackPanel host || _subtaskDragItem is not ChecklistItem item)
         return;

      var from = host.Children.IndexOf(row);
      if (from < 0 || target < 0 || target == from || target >= host.Children.Count)
         return;

      if (row.DataContext is not ChecklistSubtask subtask)
         return;

      host.Children.RemoveAt(from);
      host.Children.Insert(target, row);
      if (ReorderManual(item, subtask, target))
      {
         _subtaskReordered = true;
         return;
      }

      host.Children.RemoveAt(target);
      host.Children.Insert(from, row);
   }

   private static bool ReorderManual(ChecklistItem item, ChecklistSubtask subtask, int targetManualIndex)
   {
      var manuals = item.Subtasks.Where(entry => entry.IsManual).ToList();
      var from = manuals.FindIndex(entry => ReferenceEquals(entry, subtask));
      if (from < 0 || targetManualIndex < 0 || targetManualIndex >= manuals.Count || targetManualIndex == from)
         return false;

      manuals.RemoveAt(from);
      manuals.Insert(targetManualIndex, subtask);

      var next = 0;
      for (var i = 0; i < item.Subtasks.Count; i++)
      {
         if (!item.Subtasks[i].IsManual)
            continue;

         item.Subtasks[i] = manuals[next++];
      }

      return true;
   }

   private void SyncParentCheckBox(ChecklistItem item)
   {
      if (FindItemBorder(item) is not { } border)
         return;

      var box = border.GetVisualDescendants()
          .OfType<CheckBox>()
          .FirstOrDefault(candidate => candidate.DataContext is ChecklistItem);
      if (box is not null)
         SetCheckBox(box, item.IsCompleted);
   }

   private void SetCheckBox(CheckBox checkBox, bool completed)
   {
      if (checkBox.IsChecked == completed)
         return;

      _syncingCheck = true;
      try
      {
         checkBox.IsChecked = completed;
      }
      finally
      {
         _syncingCheck = false;
      }
   }

   private void NotifySubtasksRequired(ChecklistItem item)
   {
      var manualOpen = item.Subtasks.Any(subtask => subtask.IsManual && !subtask.IsCompleted);
      var openLinks = item.Subtasks.Count(subtask =>
          subtask.IsLink && !subtask.LinkOptional && !subtask.IsCompleted);
      var message = manualOpen
          ? "Complete the subtasks first to mark this as complete."
          : openLinks == 1
              ? "Complete the required linked checklist first to mark this as complete."
              : "Complete the required linked checklists first to mark this as complete.";
      SubtasksRequired?.Invoke(this, message);
   }

   private void OpenLinkedSubtask_Click(object? sender, RoutedEventArgs e)
   {
      if (sender is not Button button || button.DataContext is not ChecklistSubtask subtask || !subtask.IsLink)
         return;

      if (FindOwnerItem(button) is ChecklistItem item)
         SelectItem(item);

      LinkedSubtaskOpenRequested?.Invoke(this, subtask);
   }

   private void OpenCopiedFrom_Click(object? sender, RoutedEventArgs e)
   {
      if (sender is not Button { DataContext: ChecklistItem item } ||
          string.IsNullOrWhiteSpace(item.CopiedFromPath))
         return;

      CopiedFromOpenRequested?.Invoke(this, item.CopiedFromPath);
   }

   private void LinkedSubtask_Loaded(object? sender, RoutedEventArgs e)
   {
      if (sender is not Button button || button.DataContext is not ChecklistSubtask subtask)
         return;

      if (subtask.IsCompleted)
         button.Classes.Add("done");
      else
         button.Classes.Remove("done");

      if (subtask.LinkOptional)
         button.Classes.Add("optional");
      else
         button.Classes.Remove("optional");

      ToolTip.SetTip(
          button,
          subtask.LinkOptional
              ? "Open checklist. Optional — this check can be completed without it."
              : "Open checklist. Required — complete this checklist before the check.");
   }

   public void ApplyLinkedCompletion(Guid documentId, string? filePath, bool completed, string title)
   {
      var changed = false;
      foreach (var item in _checklist)
      {
         var itemChanged = false;
         foreach (var subtask in item.Subtasks ?? [])
         {
            if (!LinksTo(subtask, documentId, filePath))
               continue;

            if (!string.IsNullOrWhiteSpace(filePath) &&
                !string.Equals(subtask.LinkedFilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
               subtask.LinkedFilePath = filePath;
               itemChanged = true;
            }

            if (!string.IsNullOrWhiteSpace(title) && subtask.Title != title)
            {
               subtask.Title = title;
               itemChanged = true;
            }

            if (subtask.IsCompleted != completed)
            {
               subtask.IsCompleted = completed;
               itemChanged = true;
            }
         }

         if (!itemChanged)
            continue;

         if (!item.CanComplete)
            item.IsCompleted = false;

         RefreshVisibleItem(item);
         changed = true;
      }

      if (changed)
         MarkEdited();
   }

   private static bool LinksTo(ChecklistSubtask subtask, Guid documentId, string? filePath)
   {
      if (!subtask.IsLink)
         return false;

      if (subtask.LinkedDocumentId is Guid id && id == documentId)
         return true;

      if (string.IsNullOrWhiteSpace(subtask.LinkedFilePath) || string.IsNullOrWhiteSpace(filePath))
         return false;

      try
      {
         return string.Equals(
             Path.GetFullPath(subtask.LinkedFilePath),
             Path.GetFullPath(filePath),
             StringComparison.OrdinalIgnoreCase);
      }
      catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
      {
         return false;
      }
   }

   private static ChecklistItem? FindOwnerItem(Visual source)
   {
      for (var current = source; current is not null; current = current.GetVisualParent())
      {
         if (current is Border { DataContext: ChecklistItem item } border &&
             border.Classes.Contains("check-item"))
            return item;
      }

      return null;
   }

   private void CheckItem_Loaded(object? sender, RoutedEventArgs e)
   {
      if (sender is not Border border)
         return;

      border.Focusable = true;
      border.FocusAdorner = null;
      ApplySelection(border);
      ApplyItemState(border);
      UpdateMoveButtons(border);

      if (Equals(border.Tag, "shortcut-ready"))
         return;

      border.Tag = "shortcut-ready";
      border.AddHandler(
          PointerPressedEvent,
          CheckItem_PointerPressed,
          RoutingStrategies.Tunnel,
          handledEventsToo: true);
      border.AddHandler(
          GotFocusEvent,
          CheckItem_GotFocus,
          RoutingStrategies.Bubble,
          handledEventsToo: true);
   }

   private void CheckItem_PointerPressed(object? sender, PointerPressedEventArgs e)
   {
      if (sender is not Border border || border.DataContext is not ChecklistItem item)
         return;

      if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
         return;

      SelectItem(item);
      if (e.Source is Visual source && IsItemChrome(source, border))
         return;

      border.Focus();
      OpenItemDetails(item);
   }

   private void CheckItem_GotFocus(object? sender, RoutedEventArgs e)
   {
      if (sender is Border { DataContext: ChecklistItem item })
         SelectItem(item);
   }

   public void FocusSearch()
   {
      if (_checklist.Count == 0)
         return;

      if (!_filtersVisible)
         SetFilterSectionVisible(true);

      Dispatcher.UIThread.Post(() => SearchTextBox.Focus(), DispatcherPriority.Input);
   }

   private void ItemShortcutKeyDown(object? sender, KeyEventArgs e)
   {
      var firstPress = _itemKeysDown.Add(e.Key);
      if (e.Handled)
         return;

      var shortcut = AppShortcuts.Match(e, ShortcutScope.Checklist);
      if (shortcut is null || !CanRunItemShortcut(shortcut))
         return;

      if (_selectedItem is not { } item || !_visible.Contains(item))
         return;

      e.Handled = true;
      if (!firstPress)
         return;

      if (shortcut.Id == AppShortcuts.EditItem)
         EditItem(item);
      else if (shortcut.Id == AppShortcuts.ToggleItem)
         ToggleItem(item);
   }

   private void ItemShortcutKeyUp(object? sender, KeyEventArgs e) =>
       _itemKeysDown.Remove(e.Key);

   private bool CanRunItemShortcut(AppShortcut shortcut)
   {
      if (_documentCompleted || !IsEffectivelyVisible || _editOpen)
         return false;

      if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual focused)
         return false;

      if (!IsInside(focused, this))
         return false;

      for (var current = focused; current is not null; current = current.GetVisualParent())
      {
         if (current is TextBox or ComboBox or Avalonia.Controls.Calendar)
            return false;

         if (shortcut.Id == AppShortcuts.ToggleItem && current is Button or CheckBox)
            return false;
      }

      return true;
   }

   private void SelectItem(ChecklistItem item)
   {
      _selectedItem = item;
      foreach (var border in ChecklistItems.GetVisualDescendants().OfType<Border>())
      {
         if (border.Classes.Contains("check-item"))
            ApplySelection(border);
      }
   }

   private void ApplySelection(Border border)
   {
      if (border.DataContext == _selectedItem)
         border.Classes.Add("selected");
      else
         border.Classes.Remove("selected");
   }

   private void OpenItemDetails(ChecklistItem item)
   {
      _detailItem = item;
      PresentDetails();
   }

   private void SyncDetailLayout()
   {
      if (_detailItem is null)
         return;

      var side = UseSideDetail();
      if (side == _detailOnSide && (side ? DetailPane.IsVisible : _detailPopup is not null))
         return;

      PresentDetails();
   }

   private void HostWindow_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
   {
      if (e.Property == Window.WindowStateProperty)
         SyncDetailLayout();
   }

   private bool UseSideDetail()
   {
      if (_hostWindow?.WindowState == WindowState.FullScreen)
         return true;

      var width = Bounds.Width;
      if (width <= 1 && _hostWindow is not null)
         width = _hostWindow.ClientSize.Width;

      return width >= 1100;
   }

   private void PresentDetails()
   {
      if (_detailItem is null || !_checklist.Contains(_detailItem))
      {
         CloseDetails();
         return;
      }

      var canEdit = !_documentCompleted;
      if (UseSideDetail())
      {
         if (_detailPopup is not null)
         {
            _switchingDetail = true;
            _detailPopup.Close();
            _detailPopup = null;
            _switchingDetail = false;
         }

         DetailPane.IsVisible = true;
         SideDetail.Show(_detailItem, canEdit);
         _detailOnSide = true;
         return;
      }

      DetailPane.IsVisible = false;
      _detailOnSide = false;
      if (_detailPopup is null)
      {
         var owner = GetOwnerWindow();
         if (owner is null)
            return;

         var popup = new CheckDetailWindow();
         popup.Detail.ExplainRequested += (_, _) => ExplainOpenDetail();
         popup.Detail.EditRequested += (_, _) => EditOpenDetail();
         popup.Detail.DeleteRequested += (_, _) => DeleteOpenDetail();
         popup.Detail.CompleteRequested += (_, _) => CompleteOpenDetail();
         popup.Detail.SkipRequested += (_, _) => SkipOpenDetail();
         popup.Detail.LinkRequested += (_, subtask) =>
             LinkedSubtaskOpenRequested?.Invoke(this, subtask);
         popup.Detail.CopiedFromRequested += (_, path) =>
             CopiedFromOpenRequested?.Invoke(this, path);
         popup.Closed += (_, _) =>
         {
            if (ReferenceEquals(_detailPopup, popup))
               _detailPopup = null;

            if (!_switchingDetail)
               _detailItem = null;
         };
         _detailPopup = popup;
         popup.Show(owner);
      }

      _detailPopup.Detail.Show(_detailItem, canEdit);
      _detailPopup.Title = string.IsNullOrWhiteSpace(_detailItem.Title)
          ? "Check"
          : _detailItem.Title;
      _detailPopup.Activate();
   }

   private void SyncOpenDetail()
   {
      if (_detailItem is null)
         return;

      if (!_checklist.Contains(_detailItem))
      {
         CloseDetails();
         return;
      }

      var canEdit = !_documentCompleted;
      if (_detailOnSide && DetailPane.IsVisible)
         SideDetail.Show(_detailItem, canEdit);
      else if (_detailPopup is not null)
      {
         _detailPopup.Detail.Show(_detailItem, canEdit);
         _detailPopup.Title = string.IsNullOrWhiteSpace(_detailItem.Title)
             ? "Check"
             : _detailItem.Title;
      }
   }

   private void EditOpenDetail()
   {
      if (_detailItem is not null && !_documentCompleted)
         EditItem(_detailItem);
   }

   private void DeleteOpenDetail()
   {
      if (_detailItem is not null && !_documentCompleted)
         _ = RemoveItemAsync(_detailItem);
   }

   private void CompleteOpenDetail()
   {
      if (_detailItem is not null && !_documentCompleted)
         ToggleItem(_detailItem);
   }

   private void SkipOpenDetail()
   {
      if (_detailItem is not null && !_documentCompleted)
         ToggleSkip(_detailItem);
   }

   public bool TryCloseDetail()
   {
      if (_detailPopup is null && !DetailPane.IsVisible)
         return false;

      CloseDetails();
      return true;
   }

   private void CloseDetails()
   {
      _detailItem = null;
      _detailOnSide = false;
      DetailPane.IsVisible = false;
      if (_detailPopup is null)
         return;

      var popup = _detailPopup;
      _detailPopup = null;
      popup.Close();
   }

   private static bool IsItemChrome(Visual source, Border row)
   {
      for (var current = source; current is not null && current != row; current = current.GetVisualParent())
      {
         if (current is Button or CheckBox or TextBox)
            return true;

         if (current is Border border && border.Classes.Contains("subtask-grip"))
            return true;
      }

      return false;
   }

   private void ToggleItem(ChecklistItem item)
   {
      if (!item.IsCompleted && !item.CanComplete)
      {
         NotifySubtasksRequired(item);
         return;
      }

      item.MarkCompleted(!item.IsCompleted);
      SyncParentCheckBox(item);
      if (FindItemBorder(item) is { } border)
         ApplyItemState(border);

      if (item.IsCompleted)
         SuperDev.SuccessSound.PlayCheck();

      MarkEdited();
      if (item.IsCompleted)
         _ = OfferChecklistCompletionAsync();
   }

   private void SkipCheck_Click(object? sender, RoutedEventArgs e)
   {
      if (_documentCompleted || sender is not Button { Tag: ChecklistItem item })
         return;

      SelectItem(item);
      ToggleSkip(item);
   }

   private void ToggleSkip(ChecklistItem item)
   {
      if (_documentCompleted || !_checklist.Contains(item))
         return;

      item.MarkSkipped(!item.IsSkipped);
      SyncParentCheckBox(item);
      if (FindItemBorder(item) is { } border)
         ApplyItemState(border);

      MarkEdited();
      if (item.IsSkipped)
         _ = OfferChecklistCompletionAsync();
   }

   private void ApplyItemState(Border border)
   {
      if (border.DataContext is not ChecklistItem item)
         return;

      if (item.IsSkipped)
         border.Classes.Add("skipped");
      else
         border.Classes.Remove("skipped");

      foreach (var text in border.GetVisualDescendants().OfType<TextBlock>())
      {
         if (text.Classes.Contains("check-title"))
         {
            if (item.IsSkipped)
               text.Classes.Add("skipped");
            else
               text.Classes.Remove("skipped");
         }

         if (text.Classes.Contains("skip-meta"))
            text.IsVisible = item.IsSkipped;
      }

      foreach (var button in border.GetVisualDescendants().OfType<Button>())
      {
         if (!button.Classes.Contains("skip"))
            continue;

         if (item.IsSkipped)
            button.Classes.Add("selected");
         else
            button.Classes.Remove("selected");

         ToolTip.SetTip(
             button,
             item.IsSkipped
                 ? "Skipped. Click to include this check again."
                 : "Skip this check. It stays unfinished and is left out of progress.");
      }

      var box = border.GetVisualDescendants()
          .OfType<CheckBox>()
          .FirstOrDefault(candidate => candidate.DataContext is ChecklistItem);
      if (box is not null)
      {
         ToolTip.SetTip(
             box,
             item.IsSkipped
                 ? "Skipped. Check to mark it complete."
                 : "Toggle completion (Space)");
      }
   }

   private async Task OfferChecklistCompletionAsync()
   {
      if (_documentCompleted || _completionOfferOpen || _editOpen)
         return;

      if (!ChecklistCounts.AllCountedComplete(_checklist))
         return;

      var owner = GetOwnerWindow();
      if (owner is null)
         return;

      _completionOfferOpen = true;
      try
      {
         var dialog = new StyledDialog(
             "All checks completed",
             "Every check that still counts is done. Mark this checklist as completed?",
             "Complete",
             "Not now");
         if (!await dialog.ShowDialog<bool>(owner))
            return;

         if (_documentCompleted || !ChecklistCounts.AllCountedComplete(_checklist))
            return;

         MarkChecklistCompleted();
      }
      finally
      {
         _completionOfferOpen = false;
      }
   }

   private void MarkChecklistCompleted()
   {
      if (_documentCompleted)
         return;

      _documentCompleted = true;
      DocumentCompleted?.Invoke(this, EventArgs.Empty);
   }

   private Border? FindItemBorder(ChecklistItem item)
   {
      foreach (var border in ChecklistItems.GetVisualDescendants().OfType<Border>())
      {
         if (border.Classes.Contains("check-item") && border.DataContext == item)
            return border;
      }

      return null;
   }

   private void Save_Click(object? sender, RoutedEventArgs e) =>
       SaveRequested?.Invoke(this, EventArgs.Empty);

   public void SetManualSaveVisible(bool visible) =>
       SaveButton.IsVisible = visible;

   public void SaveAsTemplate()
   {
      if (_documentCompleted)
         return;

      CommitDetails();
      SaveAsTemplateRequested?.Invoke(this, EventArgs.Empty);
   }

   private void SaveAsTemplate_Click(object? sender, RoutedEventArgs e) =>
       SaveAsTemplate();

   private void Copy_Click(object? sender, RoutedEventArgs e)
   {
      if (_documentCompleted)
         return;

      CopyRequested?.Invoke(this, EventArgs.Empty);
   }

   private void ExportJson_Click(object? sender, RoutedEventArgs e)
   {
      ExportButton.Flyout?.Hide();
      ExportJsonRequested?.Invoke(this, EventArgs.Empty);
   }

   private void ExportCsv_Click(object? sender, RoutedEventArgs e)
   {
      ExportButton.Flyout?.Hide();
      ExportCsvRequested?.Invoke(this, EventArgs.Empty);
   }

   private void CopyText_Click(object? sender, RoutedEventArgs e)
   {
      ExportButton.Flyout?.Hide();
      CopyTextRequested?.Invoke(this, EventArgs.Empty);
   }

   public int RemoveItems(IReadOnlyList<ChecklistItem> items)
   {
      var removed = 0;
      foreach (var item in items)
      {
         if (_checklist.Remove(item))
            removed++;
      }

      if (removed > 0)
         MarkEdited();

      return removed;
   }

   public int AppendCopies(IReadOnlyList<ChecklistItem> items, string copiedFrom, string? copiedFromPath)
   {
      var added = 0;
      foreach (var item in items)
      {
         _checklist.Insert(added, CopyItem(item, copiedFrom, copiedFromPath));
         added++;
      }

      if (added > 0)
         MarkEdited();

      return added;
   }

   private static ChecklistItem CopyItem(ChecklistItem item, string copiedFrom, string? copiedFromPath) =>
       new()
       {
          Id = Guid.NewGuid(),
          Title = item.Title,
          Category = item.Category,
          Priority = item.Priority,
          Notes = item.Notes,
          Acceptance = item.Acceptance,
          ItemType = item.ItemType,
          TestReports = item.TestReports,
          Logs = item.Logs,
          CodeReferences = item.CodeReferences,
          ApiResponses = item.ApiResponses,
          PullRequestLinks = item.PullRequestLinks,
          Evidence = CopyEvidence(item.Evidence),
          Deadline = item.Deadline,
          DeadlineMinute = item.DeadlineMinute,
          NotifyBeforeMinutes = item.NotifyBeforeMinutes,
          DeadlineSilent = item.DeadlineSilent,
          Subtasks = CopySubtasks(item.Subtasks),
          IsCompleted = item.IsSkipped ? false : item.IsCompleted,
          IsSkipped = item.IsSkipped,
          CopiedFrom = copiedFrom,
          CopiedFromPath = copiedFromPath
       };

   private static List<EvidenceFile> CopyEvidence(IEnumerable<EvidenceFile>? files) =>
       (files ?? [])
           .Where(file => file is not null && !string.IsNullOrWhiteSpace(file.Path))
           .Select(file => new EvidenceFile { Path = file.Path.Trim() })
           .ToList();

   private static List<ChecklistSubtask> CopySubtasks(IEnumerable<ChecklistSubtask>? subtasks) =>
       (subtasks ?? [])
           .Select(subtask => new ChecklistSubtask
           {
              Id = Guid.NewGuid(),
              Title = subtask.Title,
              IsCompleted = subtask.IsCompleted,
              LinkedDocumentId = subtask.LinkedDocumentId,
              LinkedFilePath = subtask.LinkedFilePath,
              LinkOptional = subtask.LinkOptional
           })
           .ToList();

   private void MarkEdited()
   {
      UpdateProgress();
      ApplyView();
      DocumentEdited?.Invoke(this, EventArgs.Empty);
   }

   public void ApplyAppearance()
   {
      if (AppearanceService.Settings.Density == AppDensity.Compact)
         HeaderPanel.Classes.Add("compact");
      else
         HeaderPanel.Classes.Remove("compact");
   }

   private void Appearance_Changed(object? sender, EventArgs e) =>
       ApplyAppearance();

   protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
   {
      base.OnAttachedToVisualTree(e);
      AppearanceService.Changed -= Appearance_Changed;
      AppearanceService.Changed += Appearance_Changed;
      ApplyAppearance();
      _inputRoot = TopLevel.GetTopLevel(this);
      if (_inputRoot is Window window && !ReferenceEquals(_hostWindow, window))
      {
         if (_hostWindow is not null)
            _hostWindow.PropertyChanged -= HostWindow_PropertyChanged;

         _hostWindow = window;
         _hostWindow.PropertyChanged += HostWindow_PropertyChanged;
      }

      _inputRoot?.AddHandler(
          PointerPressedEvent,
          Title_DismissPointerPressed,
          RoutingStrategies.Tunnel,
          handledEventsToo: true);
   }

   protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
   {
      AppearanceService.Changed -= Appearance_Changed;
      _inputRoot?.RemoveHandler(PointerPressedEvent, Title_DismissPointerPressed);
      _inputRoot = null;
      if (_hostWindow is not null)
         _hostWindow.PropertyChanged -= HostWindow_PropertyChanged;
      _hostWindow = null;
      _reviewWindow?.Close();
      _explainWindow?.Close();
      base.OnDetachedFromVisualTree(e);
   }

   private void Title_DismissPointerPressed(object? sender, PointerPressedEventArgs e)
   {
      var focused = FocusedDetailBox();
      if (focused is null)
         return;

      if (e.Source is Visual source && IsInside(source, focused))
         return;

      _inputRoot?.FocusManager?.Focus(null, NavigationMethod.Pointer, KeyModifiers.None);
   }

   private static bool IsInside(Visual source, Visual target)
   {
      var current = source;
      while (current is not null)
      {
         if (current == target)
            return true;

         current = current.GetVisualParent();
      }

      return false;
   }

   private TextBox? FocusedDetailBox() =>
       WorkflowTitleBox.IsFocused ? WorkflowTitleBox : null;

   public void CommitDetails() =>
       CommitTitle();

   private void Title_LostFocus(object? sender, RoutedEventArgs e) =>
       CommitTitle();

   private void AddDescription_Click(object? sender, RoutedEventArgs e) =>
       _ = EditDescriptionAsync();

   private void ViewDescription_Click(object? sender, RoutedEventArgs e) =>
       _ = ShowDescriptionAsync();

   private async Task EditDescriptionAsync()
   {
      if (_documentCompleted)
         return;

      await OpenDescriptionAsync(editing: true);
   }

   private async Task ShowDescriptionAsync()
   {
      if (string.IsNullOrWhiteSpace(Description))
         return;

      await OpenDescriptionAsync(editing: false);
   }

   private async Task OpenDescriptionAsync(bool editing)
   {
      if (_descriptionOpen)
         return;

      var owner = GetOwnerWindow();
      if (owner is null)
         return;

      _descriptionOpen = true;
      try
      {
         var dialog = new DescriptionWindow(WorkTitle, Description, editing, canEdit: !_documentCompleted);
         var result = await dialog.ShowDialog<string?>(owner);
         if (result is null)
            return;

         var next = CleanContext(result);
         if (next == Description)
            return;

         Description = next;
         ApplyDescription();
         DocumentEdited?.Invoke(this, EventArgs.Empty);
      }
      finally
      {
         _descriptionOpen = false;
      }
   }

   private void ApplyDescription()
   {
      var text = Description ?? string.Empty;
      var hasDescription = text.Length > 0;
      AddDescriptionButton.IsVisible = !hasDescription;
      DescriptionPreviewButton.IsVisible = hasDescription;
      DescriptionPreview.Text = CollapsePreview(text);
   }

   private static string CollapsePreview(string text)
   {
      if (text.Length == 0)
         return string.Empty;

      return string.Join(
          ' ',
          text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
   }

   private void AddDetails_Click(object? sender, RoutedEventArgs e) =>
       _ = EditWorkLinksAsync();

   private void EditDetails_Click(object? sender, RoutedEventArgs e) =>
       _ = EditWorkLinksAsync();

   private async Task EditWorkLinksAsync()
   {
      if (_documentCompleted || _linksOpen)
         return;

      var owner = GetOwnerWindow();
      if (owner is null)
         return;

      _linksOpen = true;
      try
      {
         var dialog = new WorkLinksWindow(BranchName, PullRequestUrl);
         var result = await dialog.ShowDialog<WorkLinks?>(owner);
         if (result is null)
            return;

         var changed = result.Branch != BranchName ||
                       result.PullRequest != PullRequestUrl;
         BranchName = result.Branch;
         PullRequestUrl = result.PullRequest;
         ApplyWorkLinks();
         if (changed)
            DocumentEdited?.Invoke(this, EventArgs.Empty);
      }
      finally
      {
         _linksOpen = false;
      }
   }

   private void ApplyWorkLinks()
   {
      var hasBranch = !string.IsNullOrWhiteSpace(BranchName);
      var hasPullRequest = !string.IsNullOrWhiteSpace(PullRequestUrl);

      BranchLine.IsVisible = hasBranch;
      BranchValue.Text = BranchName;

      PullRequestLine.IsVisible = hasPullRequest;
      PullRequestLinkText.Text = PullRequestUrl;
      ToolTip.SetTip(PullRequestLink, PullRequestUrl);

      var hasDetails = hasBranch || hasPullRequest;
      DetailsPanel.IsVisible = hasDetails;
      AddDetailsButton.IsVisible = !hasDetails;
   }

   private async void OpenPullRequest_Click(object? sender, RoutedEventArgs e)
   {
      if (ExternalLink.TryOpen(PullRequestUrl))
         return;

      var owner = GetOwnerWindow();
      if (owner is null)
         return;

      var dialog = new StyledDialog("Super Dev", "That PR link could not be opened.");
      await dialog.ShowDialog(owner);
   }

   private void CommitTitle()
   {
      var title = WorkflowTitleBox.Text?.Trim() ?? string.Empty;
      if (title.Length == 0)
      {
         WorkflowTitleBox.Text = WorkTitle;
         return;
      }

      if (title == WorkTitle)
      {
         if (WorkflowTitleBox.Text != WorkTitle)
            WorkflowTitleBox.Text = WorkTitle;
         return;
      }

      WorkTitle = title;
      WorkflowTitleBox.Text = title;
      DocumentEdited?.Invoke(this, EventArgs.Empty);
   }

   private void Search_TextChanged(object? sender, TextChangedEventArgs e) =>
       ApplyView();

   private void View_Changed(object? sender, SelectionChangedEventArgs e)
   {
      ApplyPriorityFilterColor();
      ApplyView();
   }

   private void ApplyPriorityFilterColor()
   {
      PriorityFilterBox.Foreground = PriorityFilterBox.SelectedIndex switch
      {
         1 => PriorityAppearance.Critical,
         2 => PriorityAppearance.Required,
         3 => PriorityAppearance.Conditional,
         4 => PriorityAppearance.Review,
         _ => PriorityAppearance.Neutral
      };
   }

   private void CategoryMeta_Loaded(object? sender, RoutedEventArgs e)
   {
      if (sender is not TextBlock text || text.DataContext is not ChecklistItem item)
         return;

      text.IsVisible = !string.IsNullOrWhiteSpace(item.Category);
   }

   private void SkipMeta_Loaded(object? sender, RoutedEventArgs e)
   {
      if (sender is not TextBlock text || text.DataContext is not ChecklistItem item)
         return;

      text.IsVisible = item.IsSkipped;
   }

   private void DeadlineSeparator_Loaded(object? sender, RoutedEventArgs e)
   {
      if (sender is not TextBlock text || text.DataContext is not ChecklistItem item)
         return;

      text.IsVisible = item.Deadline is not null;
   }

   private void ItemDeadline_Loaded(object? sender, RoutedEventArgs e)
   {
      if (sender is not TextBlock text || text.DataContext is not ChecklistItem item)
         return;

      if (item.Deadline is not { } date)
      {
         text.IsVisible = false;
         return;
      }

      ApplyItemDeadline(text, item);
   }

   public void RefreshDeadlineAppearance()
   {
      ApplyDeadlineLink(DeadlineButton, Deadline);
      if (!_viewReady)
         return;

      foreach (var text in this.GetVisualDescendants().OfType<TextBlock>())
      {
         if (!text.Classes.Contains("item-deadline") || text.DataContext is not ChecklistItem item)
            continue;

         ApplyItemDeadline(text, item);
      }
   }

   private static void ApplyItemDeadline(TextBlock text, ChecklistItem item)
   {
      if (item.Deadline is not { } date)
      {
         text.IsVisible = false;
         SetLate(text, false);
         return;
      }

      var label = $"Deadline - {DeadlineValue.Format(date, item.DeadlineMinute)}";
      var notify = DeadlineValue.NotifyText(item.NotifyBeforeMinutes);
      if (notify.Length > 0)
         label = $"{label} · {notify}";
      if (item.DeadlineSilent)
         label = $"{label} · Silent";
      text.Text = label;
      text.IsVisible = true;
      SetLate(text, DeadlineValue.IsPast(date, item.DeadlineMinute));
   }

   private static void SetLate(Control control, bool late)
   {
      if (late)
         control.Classes.Add("late");
      else
         control.Classes.Remove("late");
   }

   private static void PriorityChip_Loaded(object? sender, RoutedEventArgs e)
   {
      if (sender is not Border chip || chip.DataContext is not ChecklistItem item)
         return;

      chip.Classes.Remove("critical");
      chip.Classes.Remove("required");
      chip.Classes.Remove("conditional");
      chip.Classes.Remove("review");
      chip.Classes.Add(PriorityAppearance.ClassName(item.Priority));
   }

   /// <summary>
   /// Shows or hides search, status, priority, and sort.
   /// A later interface-density setting can call this.
   /// </summary>
   public void SetFilterSectionVisible(bool visible)
   {
      _filtersVisible = visible;
      ToolTip.SetTip(FilterToggleButton, visible ? "Hide filters" : "Show filters");
      if (visible)
         FilterToggleButton.Classes.Add("selected");
      else
         FilterToggleButton.Classes.Remove("selected");
      UpdateItemActions();

      if (_viewReady)
         ApplyView();
   }

   private void FilterToggle_Click(object? sender, RoutedEventArgs e) =>
       SetFilterSectionVisible(!_filtersVisible);

   private void ResetView_Click(object? sender, RoutedEventArgs e)
   {
      SearchTextBox.Text = string.Empty;
      StatusFilterBox.SelectedIndex = 0;
      PriorityFilterBox.SelectedIndex = 0;
      SortBox.SelectedIndex = 0;
      ApplyView();
   }

   private void ApplyView()
   {
      if (!_viewReady)
         return;

      var matches = _checklist.Select((item, index) => (item, index));

      var search = SearchTextBox.Text?.Trim();
      if (!string.IsNullOrEmpty(search))
      {
         matches = matches.Where(entry =>
             entry.item.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             entry.item.Category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             entry.item.Notes.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             (entry.item.Acceptance?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
             (entry.item.ItemType?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
             entry.item.MatchesReference(search) ||
             (entry.item.Evidence ?? []).Any(file =>
                 file is not null &&
                 (EvidenceKind.DisplayName(file.Path).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                  file.Path.Contains(search, StringComparison.OrdinalIgnoreCase))) ||
             (entry.item.Subtasks ?? []).Any(subtask =>
                 subtask.Title.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
             (entry.item.Deadline is { } deadline &&
              DeadlineValue.Format(deadline, entry.item.DeadlineMinute).Contains(search, StringComparison.OrdinalIgnoreCase)));
      }

      matches = (StatusFilter)StatusFilterBox.SelectedIndex switch
      {
         StatusFilter.Open => matches.Where(entry => !entry.item.IsCompleted && !entry.item.IsSkipped),
         StatusFilter.Completed => matches.Where(entry => entry.item.IsCompleted),
         StatusFilter.Skipped => matches.Where(entry => entry.item.IsSkipped),
         _ => matches
      };

      matches = (PriorityFilter)PriorityFilterBox.SelectedIndex switch
      {
         PriorityFilter.Critical => matches.Where(entry => entry.item.Priority == ChecklistPriority.Critical),
         PriorityFilter.Required => matches.Where(entry => entry.item.Priority == ChecklistPriority.Required),
         PriorityFilter.Conditional => matches.Where(entry => entry.item.Priority == ChecklistPriority.Conditional),
         PriorityFilter.Review => matches.Where(entry => entry.item.Priority == ChecklistPriority.Review),
         _ => matches
      };

      matches = (ChecklistSort)SortBox.SelectedIndex switch
      {
         ChecklistSort.Priority => matches
             .OrderBy(entry => entry.item.Priority)
             .ThenBy(entry => entry.index),
         ChecklistSort.Category => matches
             .OrderBy(entry => entry.item.Category, StringComparer.OrdinalIgnoreCase)
             .ThenBy(entry => entry.index),
         ChecklistSort.Title => matches
             .OrderBy(entry => entry.item.Title, StringComparer.OrdinalIgnoreCase)
             .ThenBy(entry => entry.index),
         _ => matches
      };

      var visible = matches.Select(entry => entry.item).ToList();
      if (visible.Count == _visible.Count && visible.SequenceEqual(_visible))
      {
         UpdateViewCount(visible.Count);
         KeepSelectionVisible();
         UpdateItemActions();
         UpdateMoveButtons();
         SyncOpenDetail();
         return;
      }

      _visible.Clear();
      foreach (var item in visible)
         _visible.Add(item);

      KeepSelectionVisible();
      UpdateViewCount(visible.Count);
      UpdateItemActions();
      SyncOpenDetail();
      Dispatcher.UIThread.Post(UpdateMoveButtons, DispatcherPriority.Loaded);
   }

   private void UpdateMoveButtons()
   {
      foreach (var border in ChecklistItems.GetVisualDescendants().OfType<Border>())
      {
         if (border.Classes.Contains("check-item"))
            UpdateMoveButtons(border);
      }
   }

   private void UpdateMoveButtons(Border border)
   {
      if (border.DataContext is not ChecklistItem item)
         return;

      var index = _visible.IndexOf(item);
      if (index < 0)
         return;

      foreach (var button in border.GetVisualDescendants().OfType<Button>())
      {
         if (!button.Classes.Contains("move"))
            continue;

         var moveUp = button.Content as string == "↑";
         button.IsVisible = moveUp
             ? index > 0
             : index < _visible.Count - 1;
      }
   }

   private void UpdateItemActions()
   {
      var hasItems = _checklist.Count > 0;
      EmptyAddButton.IsVisible = !hasItems;
      CopyButton.IsVisible = hasItems;
      DeleteChecksButton.IsVisible = hasItems;
      RearrangeButton.IsVisible = _checklist.Count > 1;
      FilterToggleButton.IsVisible = hasItems;
      FiltersPanel.IsVisible = hasItems && _filtersVisible;
      ChecklistBody.RowSpacing = FiltersPanel.IsVisible ? 14 : 0;

      if (!hasItems)
         CollapsedCountText.IsVisible = false;

      ApplyCompletedChrome();
   }

   private void ApplyCompletedChrome()
   {
      var locked = _documentCompleted;
      var incompleteRequired = IncompleteRequiredCount();
      CompleteButton.Content = locked ? "Reopen" : "✓  Complete";
      CompleteButton.IsVisible = _checklist.Count > 0 || locked;
      CompleteButton.IsEnabled = locked || incompleteRequired == 0;
      var completeTip = locked || incompleteRequired == 0
          ? null
          : incompleteRequired == 1
              ? "1 Critical or Required check is still open."
              : $"{incompleteRequired} Critical or Required checks are still open.";
      ToolTip.SetTip(CompleteButton, completeTip);
      AddCheckButton.IsEnabled = !locked;
      SaveAsTemplateButton.IsEnabled = !locked;
      CopyButton.IsEnabled = !locked && _checklist.Count > 0;
      DeleteChecksButton.IsEnabled = !locked && _checklist.Count > 0;
      RearrangeButton.IsEnabled = !locked && _checklist.Count > 1;
      WorkflowTitleBox.IsReadOnly = locked;
      WorkflowTitleBox.IsHitTestVisible = !locked;
      if (locked)
         WorkflowTitleBox.Classes.Add("locked");
      else
         WorkflowTitleBox.Classes.Remove("locked");
      DeadlineButton.IsEnabled = !locked;
      ClearDeadlineButton.IsEnabled = !locked;
      ReminderButton.IsEnabled = !locked;
      ClearReminderButton.IsEnabled = !locked;
      AddDetailsButton.IsEnabled = !locked;
      EditDetailsButton.IsEnabled = !locked;
      AddDescriptionButton.IsEnabled = !locked;
      if (locked)
         ChecklistItems.Classes.Add("locked");
      else
         ChecklistItems.Classes.Remove("locked");
      SyncOpenDetail();
   }

   private void ApplyShortcutTips()
   {
      ToolTip.SetTip(AddCheckButton, AppShortcuts.WithShortcut(AppShortcuts.AddItem));
      ToolTip.SetTip(SaveButton, AppShortcuts.WithShortcut(AppShortcuts.Save));
      ToolTip.SetTip(SaveAsTemplateButton, AppShortcuts.WithShortcut(AppShortcuts.SaveAsTemplate, "Save this checklist as a home template"));
      ToolTip.SetTip(RearrangeButton, AppShortcuts.WithShortcut(AppShortcuts.Rearrange, "Drag checks into a new order"));
      ToolTip.SetTip(SearchTextBox, AppShortcuts.WithShortcut(AppShortcuts.Search));
   }

   private void KeepSelectionVisible()
   {
      if (_selectedItem is not null && !_visible.Contains(_selectedItem))
         _selectedItem = null;
   }

   private void UpdateViewCount(int shown)
   {
      var total = _checklist.Count;
      var narrowed = HasSearchOrFilter();
      var count = narrowed ? $"{shown} of {total}" : string.Empty;
      ViewCountText.Text = count;
      ViewCountText.IsVisible = narrowed && _filtersVisible;
      CollapsedCountText.Text = count;
      CollapsedCountText.IsVisible = narrowed && !_filtersVisible;
      EmptyFilterText.IsVisible = total > 0 && shown == 0;

      if (SortBox.SelectedIndex <= 0 && shown > 1)
         ChecklistItems.Classes.Add("reorder");
      else
         ChecklistItems.Classes.Remove("reorder");
   }

   private bool HasSearchOrFilter() =>
       !string.IsNullOrWhiteSpace(SearchTextBox.Text) ||
       StatusFilterBox.SelectedIndex > 0 ||
       PriorityFilterBox.SelectedIndex > 0;

   public void RearrangeChecks()
   {
      if (_documentCompleted)
         return;

      _ = RearrangeAsync();
   }

   private void Rearrange_Click(object? sender, RoutedEventArgs e) =>
       RearrangeChecks();

   private async Task RearrangeAsync()
   {
      if (_checklist.Count < 2)
         return;

      var owner = GetOwnerWindow();
      if (owner is null)
         return;

      var dialog = new ReorderChecksWindow(_checklist.ToList());
      var ordered = await dialog.ShowDialog<ChecklistItem[]?>(owner);
      if (ordered is null)
         return;

      ApplyItemOrder(ordered);
   }

   private void ApplyItemOrder(IReadOnlyList<ChecklistItem> ordered)
   {
      if (ordered.Count != _checklist.Count)
         return;

      var changed = false;
      for (var target = 0; target < ordered.Count; target++)
      {
         var from = _checklist.IndexOf(ordered[target]);
         if (from < 0)
            return;

         if (from == target)
            continue;

         _checklist.Move(from, target);
         changed = true;
      }

      if (!changed)
         return;

      if (SortBox.SelectedIndex != 0)
         SortBox.SelectedIndex = 0;

      MarkEdited();
   }

   private void MoveUp_Click(object? sender, RoutedEventArgs e) =>
       MoveCheck(sender, -1);

   private void MoveDown_Click(object? sender, RoutedEventArgs e) =>
       MoveCheck(sender, 1);

   private void MoveCheck(object? sender, int direction)
   {
      if (SortBox.SelectedIndex > 0)
         return;

      if (sender is not Button button || button.Tag is not ChecklistItem item)
         return;

      var visibleIndex = _visible.IndexOf(item);
      var neighborIndex = visibleIndex + direction;
      if (visibleIndex < 0 || neighborIndex < 0 || neighborIndex >= _visible.Count)
         return;

      var from = _checklist.IndexOf(item);
      var to = _checklist.IndexOf(_visible[neighborIndex]);
      if (from < 0 || to < 0 || from == to)
         return;

      _checklist.Move(from, to);
      _visible.Move(visibleIndex, neighborIndex);
      MarkEdited();
   }

   private void RefreshVisibleItem(ChecklistItem item)
   {
      var index = _visible.IndexOf(item);
      if (index < 0)
         return;

      _visible.RemoveAt(index);
      _visible.Insert(index, item);
   }

   private void ReviewWithAi_Click(object? sender, RoutedEventArgs e) =>
       OpenReview();

   private void ExplainCheck_Click(object? sender, RoutedEventArgs e)
   {
      if (sender is Button { Tag: ChecklistItem item })
         OpenExplain(item);
   }

   private void ExplainOpenDetail()
   {
      if (_detailItem is not null)
         OpenExplain(_detailItem);
   }

   private void OpenExplain(ChecklistItem item)
   {
      var owner = DialogOwner();
      if (owner is null || EnsureAiReady is null || ExplainTask is null)
         return;

      if (_explainWindow is not null)
      {
         _explainWindow.Explain(CreateExplainSession(item));
         _explainWindow.Activate();
         return;
      }

      _ = OpenExplainAsync(owner, item);
   }

   private async Task OpenExplainAsync(Window owner, ChecklistItem item)
   {
      if (_explainOpening || EnsureAiReady is null || ExplainTask is null)
         return;

      _explainOpening = true;
      try
      {
         if (!await EnsureAiReady(owner, CancellationToken.None))
            return;

         if (_explainWindow is not null)
         {
            _explainWindow.Explain(CreateExplainSession(item));
            _explainWindow.Activate();
            return;
         }

         var window = new TaskExplainWindow(CreateExplainSession(item));
         _explainWindow = window;
         window.Closed += (_, _) =>
         {
            if (ReferenceEquals(_explainWindow, window))
               _explainWindow = null;
         };
         window.Show(owner);
      }
      finally
      {
         _explainOpening = false;
      }
   }

   private TaskExplainSession CreateExplainSession(ChecklistItem item)
   {
      var explain = ExplainTask ?? throw new InvalidOperationException("Local AI is not available for this checklist.");
      var id = item.Id;
      return new TaskExplainSession
      {
         ItemId = id,
         Capture = () =>
         {
            var current = _checklist.FirstOrDefault(entry => entry.Id == id) ?? item;
            return TaskExplainMapper.Create(
                WorkTitle,
                _workType,
                TaskDescription,
                AiPlatform,
                AiFramework,
                current);
         },
         Explain = (request, token) => explain(request, token)
      };
   }

   private void OpenReview()
   {
      var owner = GetOwnerWindow();
      if (owner is null || EnsureAiReady is null || ReviewChecklist is null)
         return;

      if (_reviewWindow is not null)
      {
         _reviewWindow.Activate();
         return;
      }

      _ = OpenReviewAsync(owner);
   }

   private async Task OpenReviewAsync(Window owner)
   {
      if (EnsureAiReady is null || ReviewChecklist is null)
         return;

      if (!await EnsureAiReady(owner, CancellationToken.None))
         return;

      if (_reviewWindow is not null)
      {
         _reviewWindow.Activate();
         return;
      }

      var session = new ChecklistReviewSession
      {
         Result = _reviewResult,
         Capture = CaptureReviewRequest,
         Review = (request, token) => ReviewChecklist(request, token),
         Apply = ApplyReviewFinding,
         Revert = RevertReviewFinding,
         Dismiss = finding => ChecklistReviewApplicator.Dismiss(finding),
         Reveal = RevealItem,
         Find = id => _checklist.FirstOrDefault(item => item.Id == id),
         CanApply = () => !_documentCompleted
      };
      var window = new ChecklistReviewWindow(session);
      _reviewWindow = window;
      window.Closed += (_, _) =>
      {
         _reviewResult = session.Result;
         if (ReferenceEquals(_reviewWindow, window))
            _reviewWindow = null;
      };
      window.Show(owner);
   }

   private ChecklistReviewRequest CaptureReviewRequest() =>
       ChecklistReviewMapper.CreateRequest(
           WorkTitle,
           _workType,
           TaskDescription,
           AiPlatform,
           AiFramework,
           _templateName,
           _checklist);

   private ReviewApplyResult ApplyReviewFinding(ChecklistReviewFinding finding)
   {
      if (_documentCompleted)
         return ReviewApplyResult.None("Reopen the checklist before applying suggestions.");

      var result = ChecklistReviewApplicator.Apply(_checklist, finding);
      if (result.Changed)
         MarkEdited();

      return result;
   }

   private ReviewApplyResult RevertReviewFinding(ChecklistReviewFinding finding)
   {
      if (_documentCompleted)
         return ReviewApplyResult.None("Reopen the checklist before changing suggestions.");

      var result = ChecklistReviewApplicator.Revert(_checklist, finding);
      if (result.Changed)
         MarkEdited();

      return result;
   }

   public bool RevealItem(Guid id)
   {
      var item = _checklist.FirstOrDefault(entry => entry.Id == id);
      if (item is null)
         return false;

      if (!_visible.Contains(item))
      {
         SearchTextBox.Text = string.Empty;
         StatusFilterBox.SelectedIndex = 0;
         PriorityFilterBox.SelectedIndex = 0;
         ApplyView();
      }

      SelectItem(item);
      Dispatcher.UIThread.Post(() =>
      {
         foreach (var border in ChecklistItems.GetVisualDescendants().OfType<Border>())
         {
            if (border.Classes.Contains("check-item") && ReferenceEquals(border.DataContext, item))
            {
               border.BringIntoView();
               break;
            }
         }
      }, DispatcherPriority.Loaded);
      return true;
   }

   private void AddCheck_Click(object? sender, RoutedEventArgs e) =>
       AddCheck();

   public void AddCheck()
   {
      if (_documentCompleted)
         return;
      _ = AddCheckAsync();
   }

   private IReadOnlyList<string> TakenTitles(ChecklistItem? except = null) =>
       _checklist
           .Where(item => !ReferenceEquals(item, except))
           .Select(item => item.Title)
           .ToList();

   private void PrepareCheckDialog(AddCheckWindow dialog)
   {
      dialog.ChecklistTitle = WorkTitle;
      dialog.ChecklistWorkType = _workType;
      dialog.EnsureAiReady = EnsureAiReady;
      dialog.GenerateOneCheck = GenerateOneCheck;
   }

   private async Task AddCheckAsync()
   {
      var owner = GetOwnerWindow();
      if (owner is null)
         return;

      var dialog = new AddCheckWindow(TakenTitles());
      PrepareCheckDialog(dialog);
      dialog.RequestLink = RequestLink;
      var result = await dialog.ShowDialog<ChecklistItem?>(owner);

      if (result is null)
         return;

      _checklist.Insert(0, result);
      MarkEdited();
   }

   private async void ViewNotes_Click(object? sender, RoutedEventArgs e)
   {
      if (sender is not Button button ||
          button.Tag is not ChecklistItem item ||
          !item.HasNotes)
         return;

      var owner = GetOwnerWindow();
      if (owner is null)
         return;

      var dialog = new NotesWindow(item.Title, item.Detail, item.Notes);
      await dialog.ShowDialog(owner);
   }

   private async void ViewAcceptance_Click(object? sender, RoutedEventArgs e)
   {
      if (sender is not Button button ||
          button.Tag is not ChecklistItem item ||
          !item.HasAcceptance)
         return;

      var owner = GetOwnerWindow();
      if (owner is null)
         return;

      var detail = string.IsNullOrWhiteSpace(item.Detail) ? "Acceptance" : "Acceptance · " + item.Detail;
      var dialog = new NotesWindow(item.Title, detail, item.Acceptance!);
      await dialog.ShowDialog(owner);
   }

   private async void ViewReference_Click(object? sender, RoutedEventArgs e)
   {
      if (sender is not Button button || button.Tag is not CheckReference reference)
         return;

      var owner = GetOwnerWindow();
      if (owner is null)
         return;

      var detail = string.IsNullOrWhiteSpace(reference.Detail)
          ? reference.Label
          : reference.Label + " · " + reference.Detail;
      var dialog = new NotesWindow(reference.Title, detail, reference.Text);
      await dialog.ShowDialog(owner);
   }

   private void EditCheck_Click(object? sender, RoutedEventArgs e)
   {
      if (sender is not Button button ||
          button.Tag is not ChecklistItem item)
         return;

      SelectItem(item);
      EditItem(item);
   }

   private void EditItem(ChecklistItem item) =>
       _ = EditItemAsync(item);

   private async Task EditItemAsync(ChecklistItem item)
   {
      if (_editOpen || !_checklist.Contains(item))
         return;

      var owner = DialogOwner();
      if (owner is null)
         return;

      _editOpen = true;
      try
      {
         var dialog = new AddCheckWindow(item, TakenTitles(item));
         PrepareCheckDialog(dialog);
         dialog.RequestLink = RequestLink;
         var result = await dialog.ShowDialog<ChecklistItem?>(owner);
         if (result is null)
            return;

         item.Title = result.Title;
         item.Category = result.Category;
         item.Priority = result.Priority;
         item.Notes = result.Notes;
         item.Acceptance = result.Acceptance;
         item.ItemType = result.ItemType;
         item.CopyReferencesFrom(result);
         item.Evidence = result.Evidence ?? [];
         item.Deadline = result.Deadline;
         item.DeadlineMinute = result.Deadline is null ? null : result.DeadlineMinute;
         item.NotifyBeforeMinutes = result.Deadline is null ? null : result.NotifyBeforeMinutes;
         item.DeadlineSilent = result.Deadline is not null && result.DeadlineSilent;
         item.Subtasks = result.Subtasks;
         item.MarkCompleted(result.IsCompleted);

         MarkEdited();
         RefreshVisibleItem(item);
      }
      finally
      {
         _editOpen = false;
      }
   }

   private async void RemoveCheck_Click(object? sender, RoutedEventArgs e)
   {
      if (sender is not Button button ||
          button.Tag is not ChecklistItem item)
         return;

      await RemoveItemAsync(item);
   }

   private async Task RemoveItemAsync(ChecklistItem item)
   {
      if (_documentCompleted || !_checklist.Contains(item))
         return;

      var owner = DialogOwner();
      if (owner is null)
         return;

      var dialog = new StyledDialog(
          "Remove checklist item?",
          $"Are you sure you want to remove \"{item.Title}\"?",
          "Remove",
          "Cancel",
          danger: true);

      var result = await dialog.ShowDialog<bool>(owner);

      if (!result || !_checklist.Contains(item))
         return;

      _checklist.Remove(item);
      MarkEdited();
   }

   private void DeleteChecks_Click(object? sender, RoutedEventArgs e) =>
       DeleteChecks();

   public void DeleteChecks()
   {
      if (_documentCompleted || _checklist.Count == 0)
         return;

      _ = DeleteChecksAsync();
   }

   private async Task DeleteChecksAsync()
   {
      var owner = GetOwnerWindow();
      if (owner is null)
         return;

      var checks = _checklist.ToList();
      var picker = new DeleteChecksWindow(checks);
      var indexes = await picker.ShowDialog<List<int>?>(owner);
      if (indexes is null || indexes.Count == 0)
         return;

      var selected = indexes
          .Where(index => index >= 0 && index < checks.Count)
          .Distinct()
          .Select(index => checks[index])
          .ToList();
      if (selected.Count == 0)
         return;

      var message = selected.Count == 1
          ? $"Are you sure you want to remove \"{selected[0].Title}\"?"
          : $"Are you sure you want to remove these {selected.Count} checks?";

      var confirm = new StyledDialog(
          "Remove checklist items?",
          message,
          "Remove",
          "Cancel",
          danger: true);

      if (!await confirm.ShowDialog<bool>(owner))
         return;

      RemoveItems(selected);
   }

   private async void Complete_Click(object? sender, RoutedEventArgs e)
   {
      if (_documentCompleted)
      {
         _documentCompleted = false;
         ApplyCompletedChrome();
         DocumentReopened?.Invoke(this, EventArgs.Empty);
         return;
      }

      var owner = GetOwnerWindow();
      if (owner is null)
         return;

      var incompleteRequired = IncompleteRequiredCount();

      if (incompleteRequired > 0)
      {
         var dialog = new StyledDialog(
             "Checklist incomplete",
             $"You still have {incompleteRequired} " +
             "Critical/Required check(s) incomplete.");

         await dialog.ShowDialog(owner);
         return;
      }

      // The document flag is stored by the window. It is not written until Save.
      MarkChecklistCompleted();

      var successDialog = new StyledDialog(
          "Checklist complete",
          "All required checks have been completed. Save the checklist to keep this.");

      await successDialog.ShowDialog(owner);
   }
}
