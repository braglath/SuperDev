using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SuperDev.Views;

public partial class DescriptionWindow : Window
{
   private readonly bool _canEdit;
   private readonly bool _openedToEdit;
   private bool _editing;
   private bool _closed;

   public DescriptionWindow(string? checklistTitle, string? description, bool editing, bool canEdit)
   {
      InitializeComponent();
      _canEdit = canEdit;
      _openedToEdit = editing;

      var title = checklistTitle?.Trim() ?? string.Empty;
      DetailText.Text = title;
      DetailText.IsVisible = title.Length > 0;

      var text = description?.Trim() ?? string.Empty;
      BodyText.Text = text;
      Editor.Text = text;
      ShowMode(editing);
   }

   protected override void OnOpened(EventArgs e)
   {
      base.OnOpened(e);
      if (!_editing)
         return;

      Editor.Focus();
      Editor.CaretIndex = Editor.Text?.Length ?? 0;
   }

   protected override void OnKeyDown(KeyEventArgs e)
   {
      base.OnKeyDown(e);
      if (e.Handled || e.Key != Key.Escape)
         return;

      e.Handled = true;
      Cancel();
   }

   private void Edit_Click(object? sender, RoutedEventArgs e)
   {
      if (!_canEdit || _closed)
         return;

      Editor.Text = BodyText.Text;
      ShowMode(editing: true);
      Editor.Focus();
      Editor.CaretIndex = Editor.Text?.Length ?? 0;
   }

   private void Save_Click(object? sender, RoutedEventArgs e) =>
       Save();

   private void Cancel_Click(object? sender, RoutedEventArgs e) =>
       Cancel();

   private void Close_Click(object? sender, RoutedEventArgs e) =>
       Cancel();

   private void ShowMode(bool editing)
   {
      _editing = editing;
      ViewPanel.IsVisible = !editing;
      EditPanel.IsVisible = editing;
      ViewActions.IsVisible = !editing;
      EditActions.IsVisible = editing;
      EditButton.IsVisible = _canEdit;

      var heading = editing
          ? (string.IsNullOrWhiteSpace(BodyText.Text) ? "Add description" : "Edit description")
          : "Description";
      Title = heading;
      Heading.Text = heading;
   }

   private void Save()
   {
      if (_closed || !_editing)
         return;

      _closed = true;
      Close(Editor.Text?.Trim() ?? string.Empty);
   }

   private void Cancel()
   {
      if (_closed)
         return;

      if (_editing && !_openedToEdit)
      {
         Editor.Text = BodyText.Text;
         ShowMode(editing: false);
         return;
      }

      _closed = true;
      Close(null);
   }
}
