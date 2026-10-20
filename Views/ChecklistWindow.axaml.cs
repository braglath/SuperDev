using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using SuperDev.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System;

namespace SuperDev.Views;

public partial class ChecklistWindow : Window
{
   private readonly WorkType _workType;
   private readonly ObservableCollection<ChecklistItem> _checklist;

   public ChecklistWindow(
    WorkType workType,
    string workTitle,
    List<ChecklistItem> checklist)
   {
      InitializeComponent();

      _workType = workType;
      _checklist = new ObservableCollection<ChecklistItem>(checklist);

      WorkflowTitle.Text = workTitle;
      WorkflowDescription.Text = FormatTemplateLine(workType, reminder: null);

      ChecklistItems.ItemsSource = _checklist;

      UpdateProgress();
   }

   private static string FormatTemplateLine(WorkType workType, string? reminder)
   {
      var template = workType switch
      {
         WorkType.Bug => "Bug Fix",
         WorkType.Feature => "Feature",
         WorkType.Development => "Development",
         WorkType.Testing => "Testing",
         WorkType.PullRequest => "Pull Request",
         WorkType.Blank => "Blank Checklist",
         WorkType.Daily => "Daily",
         _ => string.Empty
      };

      if (string.IsNullOrWhiteSpace(reminder))
         return template;

      return string.IsNullOrWhiteSpace(template)
          ? reminder.Trim()
          : $"{template} · {reminder.Trim()}";
   }

   private void UpdateProgress()
   {
      var total = _checklist.Count;

      if (total == 0)
      {
         ProgressText.Text = "0 / 0 completed";
         ProgressBar.Value = 0;
         return;
      }

      var completed = _checklist.Count(x => x.IsCompleted);

      var percentage = (double)completed / total * 100;

      ProgressText.Text = $"{completed} / {total} completed";
      ProgressBar.Value = percentage;
   }

   private void CheckBox_Click(object? sender, RoutedEventArgs e)
   {
      if (sender is not CheckBox checkBox)
         return;

      if (checkBox.DataContext is not ChecklistItem item)
         return;

      item.IsCompleted = checkBox.IsChecked == true;

      if (checkBox.Parent?.Parent is Border border)
      {
         if (item.IsCompleted)
            border.Classes.Add("completed");
         else
            border.Classes.Remove("completed");
      }

      UpdateProgress();
   }

   private async void AddCheck_Click(
       object? sender,
       RoutedEventArgs e)
   {
      var dialog = new AddCheckWindow(_checklist.Select(item => item.Title));

      var result = await dialog.ShowDialog<ChecklistItem?>(this);

      if (result == null)
         return;

      _checklist.Add(result);

      UpdateProgress();
   }

   private async void EditCheck_Click(object? sender, RoutedEventArgs e)
   {
      if (sender is not Button button)
         return;

      if (button.Tag is not ChecklistItem item)
         return;

      var dialog = new AddCheckWindow(
          item,
          _checklist.Where(other => !ReferenceEquals(other, item)).Select(other => other.Title));

      var result = await dialog.ShowDialog<ChecklistItem?>(this);

      if (result == null)
         return;

      item.Title = result.Title;
      item.Category = result.Category;
      item.Priority = result.Priority;
      item.Notes = result.Notes;
      item.IsCompleted = result.IsCompleted;

      ChecklistItems.ItemsSource = null;
      ChecklistItems.ItemsSource = _checklist;

      UpdateProgress();
   }

   private async void RemoveCheck_Click(
    object? sender,
    RoutedEventArgs e)
   {
      if (sender is not Button button)
         return;

      if (button.Tag is not ChecklistItem item)
         return;

      var dialog = new StyledDialog(
          "Remove checklist item?",
          $"Are you sure you want to remove \"{item.Title}\"?",
          "Remove",
          "Cancel",
          danger: true);

      var result = await dialog.ShowDialog<bool>(this);

      if (!result)
         return;

      _checklist.Remove(item);

      UpdateProgress();
   }

   private void ChangeWorkflow_Click(
       object? sender,
       RoutedEventArgs e)
   {
      var mainWindow = new MainWindow();

      mainWindow.Show();

      Close();
   }

   private async void Complete_Click(
    object? sender,
    RoutedEventArgs e)
   {
      var incompleteRequired = _checklist
          .Where(x =>
              !x.IsCompleted &&
              (x.Priority == ChecklistPriority.Critical ||
               x.Priority == ChecklistPriority.Required))
          .ToList();

      if (incompleteRequired.Count > 0)
      {
         var dialog = new StyledDialog(
             "Checklist incomplete",
             $"You still have {incompleteRequired.Count} " +
             "Critical/Required check(s) incomplete.");

         await dialog.ShowDialog(this);

         return;
      }

      var successDialog = new StyledDialog(
          "Checklist complete",
          "All required checks have been completed.");

      await successDialog.ShowDialog(this);
   }
}