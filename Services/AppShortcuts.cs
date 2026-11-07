using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

namespace SuperDev.Services;

public enum ShortcutGroup
{
    Application,
    Workspace,
    ChecklistItems
}

public enum ShortcutScope
{
    Window,
    Checklist
}

/// <summary>
/// Fixed shortcut catalog. The settings page, the key handler, and the menu shortcuts read this list.
/// Menu gestures use the platform modifier: Control on Windows, Command on macOS.
/// </summary>
public static class AppShortcuts
{
    public const string NewChecklist = "new-checklist";
    public const string CreateProject = "create-project";
    public const string OpenChecklist = "open-checklist";
    public const string Save = "save";
    public const string SaveAs = "save-as";
    public const string SaveAsTemplate = "save-as-template";
    public const string Rearrange = "rearrange";
    public const string Settings = "settings";
    public const string CloseTab = "close-tab";
    public const string NextTab = "next-tab";
    public const string PreviousTab = "previous-tab";
    public const string Search = "search";
    public const string ClosePopup = "close-popup";
    public const string AddItem = "add-item";
    public const string EditItem = "edit-item";
    public const string ToggleItem = "toggle-item";

    private static readonly KeyModifiers ModifierKeys =
        KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta;

    public static IReadOnlyList<AppShortcut> All { get; } =
    [
        Command(NewChecklist, "New checklist", ShortcutGroup.Application, ShortcutScope.Window, Key.N, shift: false, "Ctrl+N", "Command+N"),
        Command(CreateProject, "Create project", ShortcutGroup.Application, ShortcutScope.Window, Key.P, shift: false, "Ctrl+P", "Command+P"),
        Command(OpenChecklist, "Open checklist", ShortcutGroup.Application, ShortcutScope.Window, Key.O, shift: false, "Ctrl+O", "Command+O"),
        Command(Save, "Save checklist", ShortcutGroup.Application, ShortcutScope.Window, Key.S, shift: false, "Ctrl+S", "Command+S"),
        Command(Settings, "Settings", ShortcutGroup.Application, ShortcutScope.Window, Key.OemComma, shift: false, "Ctrl+,", "Command+,"),

        Plain(ClosePopup, "Close popup", ShortcutGroup.Workspace, ShortcutScope.Window, Key.Escape, KeyModifiers.None, "Esc", "Esc"),
        Command(CloseTab, "Close active tab", ShortcutGroup.Workspace, ShortcutScope.Window, Key.W, shift: false, "Ctrl+W", "Command+W"),
        Plain(NextTab, "Next tab", ShortcutGroup.Workspace, ShortcutScope.Window, Key.Tab, KeyModifiers.Control, "Ctrl+Tab", "Ctrl+Tab"),
        Plain(PreviousTab, "Previous tab", ShortcutGroup.Workspace, ShortcutScope.Window, Key.Tab, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+Tab", "Ctrl+Shift+Tab"),
        Command(Search, "Search checklist items", ShortcutGroup.Workspace, ShortcutScope.Window, Key.F, shift: false, "Ctrl+F", "Command+F"),

        Command(AddItem, "Add checklist item", ShortcutGroup.ChecklistItems, ShortcutScope.Window, Key.N, shift: true, "Ctrl+Shift+N", "Command+Shift+N"),
        Command(Rearrange, "Rearrange items", ShortcutGroup.ChecklistItems, ShortcutScope.Window, Key.R, shift: false, "Ctrl+R", "Command+R"),
        Command(SaveAsTemplate, "Save as template", ShortcutGroup.ChecklistItems, ShortcutScope.Window, Key.S, shift: true, "Ctrl+Shift+S", "Command+Shift+S"),
        Plain(EditItem, "Edit selected item", ShortcutGroup.ChecklistItems, ShortcutScope.Checklist, Key.F2, KeyModifiers.None, "F2", "F2"),
        Plain(ToggleItem, "Toggle selected item completion", ShortcutGroup.ChecklistItems, ShortcutScope.Checklist, Key.Space, KeyModifiers.None, "Space", "Space")
    ];

    public static AppShortcut? Match(KeyEventArgs e, ShortcutScope scope)
    {
        // Comma is only ever Settings. On macOS the same press can be reported as OemComma
        // or as a different key whose symbol is still ",", and that must not fall through
        // to New, Close, or another shortcut.
        if (IsComma(e))
        {
            var settings = All.First(item => item.Id == Settings);
            return settings.Scope == scope && settings.Matches(e) ? settings : null;
        }

        foreach (var shortcut in All)
        {
            if (shortcut.Scope == scope && shortcut.Matches(e))
                return shortcut;
        }

        return null;
    }

    public static bool IsComma(KeyEventArgs e) =>
        e.Key == Key.OemComma || e.KeySymbol == ",";

    public static bool IsClosePopup(KeyEventArgs e) =>
        All.First(item => item.Id == ClosePopup).Matches(e);

    public static string WithShortcut(string id, string? action = null)
    {
        var shortcut = All.First(item => item.Id == id);
        var name = string.IsNullOrWhiteSpace(action) ? shortcut.Action : action;
        return $"{name} ({shortcut.CurrentDisplay})";
    }

    public static string GroupTitle(ShortcutGroup group) =>
        group switch
        {
            ShortcutGroup.Application => "Application",
            ShortcutGroup.Workspace => "Workspace",
            ShortcutGroup.ChecklistItems => "Checklist Items",
            _ => group.ToString()
        };

    private static AppShortcut Command(
        string id,
        string action,
        ShortcutGroup group,
        ShortcutScope scope,
        Key key,
        bool shift,
        string windowsDisplay,
        string macDisplay)
    {
        var windows = KeyModifiers.Control | (shift ? KeyModifiers.Shift : KeyModifiers.None);
        var mac = KeyModifiers.Meta | (shift ? KeyModifiers.Shift : KeyModifiers.None);
        return new AppShortcut(id, action, group, scope, key, windows, mac, windowsDisplay, macDisplay);
    }

    private static AppShortcut Plain(
        string id,
        string action,
        ShortcutGroup group,
        ShortcutScope scope,
        Key key,
        KeyModifiers modifiers,
        string windowsDisplay,
        string macDisplay) =>
        new(id, action, group, scope, key, modifiers, modifiers, windowsDisplay, macDisplay);

    public static KeyModifiers Normalize(KeyModifiers modifiers) => modifiers & ModifierKeys;
}

public sealed class AppShortcut(
    string id,
    string action,
    ShortcutGroup group,
    ShortcutScope scope,
    Key key,
    KeyModifiers windowsModifiers,
    KeyModifiers macModifiers,
    string windowsDisplay,
    string macDisplay)
{
    public string Id { get; } = id;
    public string Action { get; } = action;
    public ShortcutGroup Group { get; } = group;
    public ShortcutScope Scope { get; } = scope;
    public Key Key { get; } = key;
    public KeyModifiers WindowsModifiers { get; } = windowsModifiers;
    public KeyModifiers MacModifiers { get; } = macModifiers;
    public string WindowsDisplay { get; } = windowsDisplay;
    public string MacDisplay { get; } = macDisplay;

    public string CurrentDisplay =>
        OperatingSystem.IsMacOS() ? MacDisplay : WindowsDisplay;

    public KeyModifiers CurrentModifiers =>
        OperatingSystem.IsMacOS() ? MacModifiers : WindowsModifiers;

    public KeyGesture MenuGesture => new(Key, CurrentModifiers);

    public bool Matches(KeyEventArgs e)
    {
        var modifiers = AppShortcuts.Normalize(e.KeyModifiers);
        if (Id == AppShortcuts.Settings && OperatingSystem.IsMacOS())
        {
            // Command+, is the shortcut. Control+, is what the menu used to advertise,
            // and either press should stay on Settings instead of moving to Home.
            if (modifiers != KeyModifiers.Meta && modifiers != KeyModifiers.Control)
                return false;
        }
        else if (modifiers != CurrentModifiers)
        {
            return false;
        }

        if (e.Key == Key)
            return true;

        return Key == Key.OemComma && e.KeySymbol == ",";
    }
}
