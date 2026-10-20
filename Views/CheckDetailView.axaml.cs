using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using SuperDev.Controls;
using SuperDev.Models;

namespace SuperDev.Views;

public partial class CheckDetailView : UserControl
{
   public event EventHandler? CloseRequested;

   public event EventHandler? EditRequested;

   public event EventHandler? DeleteRequested;

   public event EventHandler? CompleteRequested;

   public event EventHandler<ChecklistSubtask>? LinkRequested;

   public CheckDetailView()
   {
      InitializeComponent();
   }

   public void Show(ChecklistItem item, bool canEdit)
   {
      TitleText.Text = string.IsNullOrWhiteSpace(item.Title) ? "Untitled check" : item.Title;
      StatusText.Text = item.IsCompleted ? "Completed" : "Open";
      StatusText.Foreground = item.IsCompleted
          ? SolidColorBrush.Parse("#4ADE80")
          : SolidColorBrush.Parse("#E2E8F0");
      CategoryText.Text = string.IsNullOrWhiteSpace(item.Category) ? "General" : item.Category;
      PriorityText.Text = item.Priority.ToString();
      PriorityText.Foreground = PriorityAppearance.BrushFor(item.Priority);

      if (item.Deadline is { } deadline)
      {
         DeadlineText.Text = DeadlineValue.Format(deadline, item.DeadlineMinute);
         var notify = DeadlineValue.NotifyText(item.NotifyBeforeMinutes);
         NotifyText.Text = item.DeadlineSilent && !string.IsNullOrWhiteSpace(notify)
             ? $"{notify} · silent"
             : notify;
         NotifyText.IsVisible = !string.IsNullOrWhiteSpace(NotifyText.Text);
      }
      else
      {
         DeadlineText.Text = "None";
         NotifyText.IsVisible = false;
      }

      NotesText.Source = string.IsNullOrWhiteSpace(item.Notes) ? "No notes" : item.Notes;

      SubtaskList.Children.Clear();
      var manuals = item.ManualSubtasks;
      var manualCount = 0;
      foreach (var subtask in manuals)
      {
         manualCount++;
         SubtaskList.Children.Add(new TextBlock
         {
            Text = subtask.IsCompleted ? $"✓  {subtask.Title}" : $"○  {subtask.Title}",
            FontSize = 14,
            Foreground = SolidColorBrush.Parse(subtask.IsCompleted ? "#94A3B8" : "#E2E8F0"),
            TextWrapping = TextWrapping.Wrap
         });
      }

      SubtaskSection.IsVisible = manualCount > 0;
      SubtaskHeading.Text = manualCount == 1 ? "Subtask" : "Subtasks";

      LinkList.Children.Clear();
      var linkCount = 0;
      foreach (var link in item.LinkedSubtasks)
      {
         linkCount++;
         var button = new Button
         {
            Classes = { "detail-link" },
            Content = link.IsCompleted ? $"✓  {link.Title}" : link.Title,
            Tag = link
         };
         button.Click += Link_Click;
         LinkList.Children.Add(button);
      }

      LinkSection.IsVisible = linkCount > 0;
      EditButton.IsEnabled = canEdit;
      DeleteButton.IsEnabled = canEdit;
      CompleteButton.IsEnabled = canEdit;
      CompleteButton.Content = item.IsCompleted ? "Mark as open" : "Mark as completed";
   }

   private void Close_Click(object? sender, RoutedEventArgs e) =>
       CloseRequested?.Invoke(this, EventArgs.Empty);

   private void Edit_Click(object? sender, RoutedEventArgs e) =>
       EditRequested?.Invoke(this, EventArgs.Empty);

   private void Delete_Click(object? sender, RoutedEventArgs e) =>
       DeleteRequested?.Invoke(this, EventArgs.Empty);

   private void Complete_Click(object? sender, RoutedEventArgs e) =>
       CompleteRequested?.Invoke(this, EventArgs.Empty);

   private void Link_Click(object? sender, RoutedEventArgs e)
   {
      if (sender is Button { Tag: ChecklistSubtask subtask })
         LinkRequested?.Invoke(this, subtask);
   }
}
