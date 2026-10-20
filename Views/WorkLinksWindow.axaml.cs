using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SuperDev.Views;

public sealed record WorkLinks(string Branch, string Repository, string PullRequest);

public partial class WorkLinksWindow : Window
{
   private bool _closed;

   public WorkLinksWindow(string branch, string repository, string pullRequest)
   {
      InitializeComponent();
      BranchBox.Text = branch;
      RepositoryBox.Text = repository;
      PullRequestBox.Text = pullRequest;
   }

   protected override void OnOpened(EventArgs e)
   {
      base.OnOpened(e);
      BranchBox.Focus();
      BranchBox.CaretIndex = BranchBox.Text?.Length ?? 0;
   }

   private void Save_Click(object? sender, RoutedEventArgs e) =>
       Save();

   private void Cancel_Click(object? sender, RoutedEventArgs e) =>
       Cancel();

   protected override void OnKeyDown(KeyEventArgs e)
   {
      base.OnKeyDown(e);
      if (e.Handled)
         return;

      if (e.Key is Key.Enter or Key.Return)
      {
         e.Handled = true;
         Save();
      }
      else if (e.Key == Key.Escape)
      {
         e.Handled = true;
         Cancel();
      }
   }

   private void Save()
   {
      if (_closed)
         return;

      _closed = true;
      Close(new WorkLinks(
          BranchBox.Text?.Trim() ?? string.Empty,
          RepositoryBox.Text?.Trim() ?? string.Empty,
          PullRequestBox.Text?.Trim() ?? string.Empty));
   }

   private void Cancel()
   {
      if (_closed)
         return;

      _closed = true;
      Close(null);
   }
}
