using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using SuperDev.Services;

namespace SuperDev.Views;

/// <summary>
/// Esc closes the topmost popup, then a transient main-window view, then the dialog itself.
/// Editors receive the key first because this runs on the bubble.
/// </summary>
public static class PopupDismissal
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed)
            return;

        _installed = true;
        InputElement.KeyDownEvent.AddClassHandler<Window>(OnWindowKeyDown, RoutingStrategies.Bubble);
    }

    private static void OnWindowKeyDown(Window window, KeyEventArgs e)
    {
        if (e.Handled || !AppShortcuts.IsClosePopup(e))
            return;

        if (TryCloseTopPopup(window))
        {
            e.Handled = true;
            return;
        }

        if (window is MainWindow main)
        {
            if (main.DismissTransientView())
                e.Handled = true;
            return;
        }

        e.Handled = true;
        window.Close();
    }

    private static bool TryCloseTopPopup(TopLevel top)
    {
        var popup = Topmost(top.OpenedPopups);
        if (popup is null)
            return false;

        popup.Close();
        return true;
    }

    private static Popup? Topmost(IReadOnlyList<Popup> popups)
    {
        if (popups.Count == 0)
            return null;

        var popup = popups[popups.Count - 1];
        return Topmost(popup.OpenedPopups) ?? popup;
    }
}
