using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using SuperDev.Controls;
using SuperDev.Models;
using SuperDev.Services;

namespace SuperDev.Views;

public partial class CheckDetailView : UserControl
{
   public event EventHandler? CloseRequested;

   public event EventHandler? ExplainRequested;

   public event EventHandler? EditRequested;

   public event EventHandler? DeleteRequested;

   public event EventHandler? CompleteRequested;

   public event EventHandler<ChecklistSubtask>? LinkRequested;

   public event EventHandler<string>? CopiedFromRequested;

   public bool ShowCloseButton
   {
      get => CloseButton.IsVisible;
      set => CloseButton.IsVisible = value;
   }

   public bool ActionsVisible
   {
      get => ActionBar.IsVisible;
      set => ActionBar.IsVisible = value;
   }

   public CheckDetailView()
   {
      InitializeComponent();
   }

   public void Show(ChecklistItem item, bool canEdit)
   {
      TitleText.Text = string.IsNullOrWhiteSpace(item.Title) ? "Untitled check" : item.Title;
      CopiedFromSection.IsVisible = item.HasCopiedFrom;
      CopiedFromText.IsVisible = item.CopiedFromIsPlain;
      CopiedFromText.Text = item.CopiedFrom ?? string.Empty;
      CopiedFromLink.IsVisible = item.CanOpenCopiedFrom;
      CopiedFromLink.Content = item.CopiedFrom;
      CopiedFromLink.Tag = item.CopiedFromPath;
      StatusText.Text = item.IsCompleted ? "Completed" : "Open";
      StatusText.Foreground = item.IsCompleted
          ? AppearanceService.Brush("SuccessTextBrush")
          : AppearanceService.Brush("TextSecondaryBrush");
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

      TypeSection.IsVisible = item.HasItemType;
      TypeText.Text = item.ItemType ?? string.Empty;
      NotesSection.IsVisible = item.HasNotes;
      if (item.HasNotes)
         NotesText.Source = item.Notes;
      AcceptanceSection.IsVisible = item.HasAcceptance;
      if (item.HasAcceptance)
         AcceptanceText.Source = item.Acceptance;
      ShowReferences(item);
      EvidenceSection.IsVisible = item.HasEvidence;
      EvidenceList.Files = item.Evidence;

      SubtaskList.Children.Clear();
      var manuals = item.ManualSubtasks;
      var manualCount = 0;
      foreach (var subtask in manuals)
      {
         manualCount++;
         SubtaskList.Children.Add(new TextBlock
         {
            Text = subtask.IsCompleted ? $"✓  {subtask.Title}" : $"○  {subtask.Title}",
            FontSize = AppearanceService.Font(14),
            Foreground = AppearanceService.Brush(subtask.IsCompleted ? AppearanceService.TextMutedBrush : AppearanceService.TextSecondaryBrush),
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
            Content = link.LinkLabel,
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

   private void ShowReferences(ChecklistItem item)
   {
      ReferenceList.Children.Clear();
      foreach (var reference in item.References)
      {
         ReferenceList.Children.Add(new StackPanel
         {
            Spacing = 4,
            Children =
            {
               new TextBlock
               {
                  Text = reference.Label,
                  Classes = { "detail-label" }
               },
               new NoteText
               {
                  Source = reference.Text,
                  FontSize = AppearanceService.Font(14),
                  LineHeight = 22,
                  Foreground = AppearanceService.Brush(AppearanceService.TextBodyBrush),
                  TextWrapping = TextWrapping.Wrap
               }
            }
         });
      }
   }

   private void Close_Click(object? sender, RoutedEventArgs e) =>
       CloseRequested?.Invoke(this, EventArgs.Empty);

   private void Explain_Click(object? sender, RoutedEventArgs e) =>
       ExplainRequested?.Invoke(this, EventArgs.Empty);

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

   private void CopiedFrom_Click(object? sender, RoutedEventArgs e)
   {
      if (CopiedFromLink.Tag is string path && !string.IsNullOrWhiteSpace(path))
         CopiedFromRequested?.Invoke(this, path);
   }
}
