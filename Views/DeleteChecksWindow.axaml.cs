using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SuperDev.Models;

namespace SuperDev.Views;

public partial class DeleteChecksWindow : Window
{
    public DeleteChecksWindow(IReadOnlyList<ChecklistItem> checks)
    {
        InitializeComponent();
        Picker.SetChecks(checks);
        Picker.SelectionChanged += (_, _) => UpdateActions();
        UpdateActions();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || e.Key != Key.Escape)
            return;

        e.Handled = true;
        Close(null);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) =>
        Close(null);

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        var selected = SelectedIndexes();
        if (selected.Count == 0)
            return;

        Close(selected);
    }

    private List<int> SelectedIndexes() => Picker.SelectedIndexes.ToList();

    private void UpdateActions() =>
        DeleteButton.IsEnabled = Picker.SelectedCount > 0;
}
