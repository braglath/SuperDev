using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SuperDev.Models;

namespace SuperDev.Views;

public sealed class CopyChecklistChoice
{
    public CopyChecklistChoice(int destinationIndex, IReadOnlyList<int> itemIndexes)
    {
        DestinationIndex = destinationIndex;
        ItemIndexes = itemIndexes;
    }

    public int DestinationIndex { get; }

    public IReadOnlyList<int> ItemIndexes { get; }
}

public partial class CopyChecklistWindow : Window
{
    public CopyChecklistWindow(
        IReadOnlyList<ChecklistItem> checks,
        IReadOnlyList<string> openChecklists)
    {
        InitializeComponent();
        Picker.SetChecks(checks);
        Picker.SelectionChanged += (_, _) => UpdateActions();

        var hasDestination = openChecklists.Count > 0;
        DestinationBox.IsVisible = hasDestination;
        NoDestinationText.IsVisible = !hasDestination;

        foreach (var name in openChecklists)
            DestinationBox.Items.Add(name);

        if (hasDestination)
            DestinationBox.SelectedIndex = 0;

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

    private void Destination_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdateActions();

    private void Cancel_Click(object? sender, RoutedEventArgs e) =>
        Close(null);

    private void NewFile_Click(object? sender, RoutedEventArgs e)
    {
        var selected = SelectedIndexes();
        if (selected.Count == 0)
            return;

        Close(new CopyChecklistChoice(-1, selected));
    }

    private void Copy_Click(object? sender, RoutedEventArgs e)
    {
        var selected = SelectedIndexes();
        if (selected.Count == 0 || DestinationBox.SelectedIndex < 0)
            return;

        Close(new CopyChecklistChoice(DestinationBox.SelectedIndex, selected));
    }

    private List<int> SelectedIndexes() => Picker.SelectedIndexes.ToList();

    private void UpdateActions()
    {
        var count = Picker.SelectedCount;
        NewFileButton.IsEnabled = count > 0;
        CopyButton.IsEnabled = count > 0 && DestinationBox.SelectedIndex >= 0;
    }
}
