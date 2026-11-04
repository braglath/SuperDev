using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SuperDev.Services;
using SuperDev.ViewModels;
using SuperDev.Views;

namespace SuperDev;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        AppearanceService.Install();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
            desktop.MainWindow = window;
            if (desktop is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, args) =>
                {
                    if (args is not FileActivatedEventArgs files)
                        return;

                    foreach (var file in files.Files)
                    {
                        var path = file.TryGetLocalPath();
                        if (string.IsNullOrWhiteSpace(path))
                            continue;

                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            window.OpenFromNotification(Guid.Empty, path));
                    }
                };
            }

            MacNotificationCenter.SetGuiHandler(request =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    window.OpenFromNotification(request.DocumentId, request.FilePath)));
            MacNotificationCenter.EnsureReady();
            AppearanceService.ApplyWindowIcon(window);
            SystemNotification.RegisterFileAssociation();
        }

        base.OnFrameworkInitializationCompleted();
    }
}