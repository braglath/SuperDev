using Avalonia.Input;
using SuperDev.Services;

namespace SuperDev.Tests;

public class AppShortcutTests
{
    [Fact]
    public void Close_popup_is_escape_without_modifiers()
    {
        var shortcut = Assert.Single(AppShortcuts.All, item => item.Id == AppShortcuts.ClosePopup);

        Assert.Equal(Key.Escape, shortcut.Key);
        Assert.Equal(KeyModifiers.None, shortcut.WindowsModifiers);
        Assert.Equal(KeyModifiers.None, shortcut.MacModifiers);
        Assert.Equal("Esc", shortcut.WindowsDisplay);
        Assert.Equal("Esc", shortcut.MacDisplay);
        Assert.Equal(ShortcutGroup.Workspace, shortcut.Group);
        Assert.Equal(ShortcutScope.Window, shortcut.Scope);
    }
}
