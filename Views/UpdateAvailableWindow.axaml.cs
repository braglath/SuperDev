using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SuperDev.Services;

namespace SuperDev.Views;

public sealed class UpdatePromptResult
{
    public bool OpenRelease { get; init; }
    public PreparedUpdate? Prepared { get; init; }
    public bool Install { get; init; }
    public bool PromptForNewFiles { get; init; }

    public static UpdatePromptResult Release() => new() { OpenRelease = true };

    public static UpdatePromptResult Ready(PreparedUpdate prepared, bool install, bool promptForNewFiles) => new()
    {
        Prepared = prepared,
        Install = install,
        PromptForNewFiles = promptForNewFiles
    };
}

public partial class UpdateAvailableWindow : Window
{
    private enum Stage
    {
        Offer,
        Downloading,
        Ready,
        Failed
    }

    private readonly UpdateCheckResult _update;
    private readonly bool _startDownload;
    private readonly bool _confirmWhenReady;
    private readonly Action<string> _reportStatus;
    private CancellationTokenSource? _download;
    private PreparedUpdate? _prepared;
    private Stage _stage;
    private bool _closed;
    private bool _handOff;

    public UpdateAvailableWindow(UpdateCheckResult update, bool startDownload, bool confirmWhenReady, Action<string> reportStatus)
    {
        InitializeComponent();
        _update = update;
        _confirmWhenReady = confirmWhenReady;
        _reportStatus = reportStatus;
        _startDownload = startDownload && !string.IsNullOrWhiteSpace(update.DownloadUrl);
        ShowStage(_startDownload ? Stage.Downloading : Stage.Offer);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_startDownload)
            _ = StartDownloadAsync();
        else if (PrimaryButton.IsVisible)
            PrimaryButton.Focus();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_handOff)
            return;

        var failed = _stage == Stage.Failed;
        _download?.Cancel();
        _prepared?.Cancel();
        _prepared = null;
        _closed = true;
        if (!failed)
            _reportStatus(AvailableStatus());
        base.OnClosing(e);
    }

    private async void PrimaryButton_Click(object? sender, RoutedEventArgs e)
    {
        switch (_stage)
        {
            case Stage.Offer when HasDownload:
                await StartDownloadAsync();
                break;
            case Stage.Offer:
                Finish(UpdatePromptResult.Release());
                break;
            case Stage.Ready when _prepared is { } prepared:
                _prepared = null;
                Finish(UpdatePromptResult.Ready(prepared, _update.CanInstall, _confirmWhenReady));
                break;
            default:
                Finish(null);
                break;
        }
    }

    private void SecondaryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_stage == Stage.Downloading)
        {
            SecondaryButton.IsEnabled = false;
            _download?.Cancel();
            return;
        }

        _prepared?.Cancel();
        _prepared = null;
        Finish(null);
    }

    private async Task StartDownloadAsync()
    {
        if (_download is not null)
            return;

        _download = new CancellationTokenSource();
        ShowStage(Stage.Downloading);
        var progress = new Progress<string>(Report);
        var prepared = await UpdateService.PrepareInstallAsync(_update, progress, _download.Token);
        if (_closed)
        {
            prepared.Cancel();
            return;
        }

        if (prepared.Error is not null)
        {
            if (_download.IsCancellationRequested)
            {
                Finish(null);
                return;
            }

            ShowStage(Stage.Failed);
            MessageText.Text = prepared.Error;
            _reportStatus(prepared.Error);
            return;
        }

        _prepared = prepared;
        if (_update.CanInstall && !_confirmWhenReady)
        {
            _prepared = null;
            Finish(UpdatePromptResult.Ready(prepared, install: true, promptForNewFiles: false));
            return;
        }

        ShowStage(Stage.Ready);
        _reportStatus(_update.CanInstall
            ? $"Super Dev {_update.LatestText} is ready to install."
            : $"Super Dev {_update.LatestText} is downloaded.");
    }

    private void ShowStage(Stage stage)
    {
        _stage = stage;
        DownloadProgress.IsVisible = stage == Stage.Downloading;
        SecondaryButton.IsEnabled = true;

        switch (stage)
        {
            case Stage.Offer:
                TitleText.Text = "A new version is available";
                MessageText.Text = OfferMessage(_update);
                SecondaryButton.Content = "Later";
                SecondaryButton.IsVisible = true;
                PrimaryButton.IsVisible = HasDownload || !string.IsNullOrWhiteSpace(_update.ReleaseUrl);
                PrimaryButton.Content = HasDownload ? "Download" : "View release";
                break;
            case Stage.Downloading:
                TitleText.Text = "A new version is available";
                MessageText.Text = "Downloading…";
                DownloadProgress.IsIndeterminate = true;
                DownloadProgress.Value = 0;
                SecondaryButton.Content = "Cancel";
                SecondaryButton.IsVisible = true;
                PrimaryButton.IsVisible = false;
                _reportStatus("Downloading…");
                break;
            case Stage.Ready:
                DownloadProgress.IsVisible = false;
                SecondaryButton.Content = "Later";
                SecondaryButton.IsVisible = true;
                PrimaryButton.IsVisible = true;
                if (_update.CanInstall)
                {
                    TitleText.Text = "Restart to update";
                    MessageText.Text = _update.Kind == UpdatePackageKind.WindowsMsix
                        ? $"Super Dev {_update.LatestText} is ready. Super Dev will close and Windows will open the installer."
                        : $"Super Dev {_update.LatestText} is ready. Restart now to finish installing?";
                    PrimaryButton.Content = _update.Kind == UpdatePackageKind.WindowsMsix ? "Install" : "Restart";
                }
                else
                {
                    TitleText.Text = "Update downloaded";
                    MessageText.Text = $"Super Dev {_update.LatestText} is downloaded. Open it to install.";
                    PrimaryButton.Content = "Open";
                }

                break;
            case Stage.Failed:
                TitleText.Text = "Couldn't download the update";
                SecondaryButton.IsVisible = false;
                PrimaryButton.IsVisible = true;
                PrimaryButton.Content = "Close";
                break;
        }
    }

    private void Report(string message)
    {
        if (_stage != Stage.Downloading)
            return;

        MessageText.Text = message;
        _reportStatus(message);
        if (!TryPercent(message, out var percent))
        {
            DownloadProgress.IsIndeterminate = true;
            return;
        }

        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value = percent;
    }

    private bool HasDownload => !string.IsNullOrWhiteSpace(_update.DownloadUrl);

    private string AvailableStatus()
    {
        var status = $"Super Dev {_update.LatestText} is available.";
        if (!string.IsNullOrWhiteSpace(_update.Note))
            status += " " + _update.Note;
        return status;
    }

    private static string OfferMessage(UpdateCheckResult update)
    {
        var message = $"Super Dev {update.LatestText} is available. You're using {update.CurrentText}.";
        if (!string.IsNullOrWhiteSpace(update.Note))
            message += Environment.NewLine + Environment.NewLine + update.Note;
        return message;
    }

    private static bool TryPercent(string message, out int percent)
    {
        percent = 0;
        const string prefix = "Downloading ";
        if (!message.StartsWith(prefix, StringComparison.Ordinal) || !message.EndsWith("%…", StringComparison.Ordinal))
            return false;

        return int.TryParse(message[prefix.Length..^2], out percent);
    }

    private void Finish(UpdatePromptResult? result)
    {
        if (_closed)
            return;

        _handOff = result?.Prepared is not null;
        _closed = true;
        Close(result);
    }
}
