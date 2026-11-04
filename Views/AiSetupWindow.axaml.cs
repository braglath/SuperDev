using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SuperDev.Models;
using SuperDev.Services.Ai;

namespace SuperDev.Views;

public partial class AiSetupWindow : Window
{
    private readonly AISetupManager _setup;
    private readonly AiSetupNeed _need;
    private CancellationTokenSource? _run;
    private bool _busy;

    public AiSetupWindow(AISetupManager setup, AiSetupNeed need)
    {
        _setup = setup ?? throw new ArgumentNullException(nameof(setup));
        _need = need;
        InitializeComponent();
        ExplainText.Text = _need switch
        {
            AiSetupNeed.StartRuntime =>
                "Ollama is already on this computer, but the local service is not responding. Super Dev will start it and download the model only if it is missing. AI stays on this computer, and no cloud API is used.",
            AiSetupNeed.DownloadModel =>
                "Ollama is running. The qwen3.5:0.8b model is downloaded once and stored on this computer. No cloud API is used.",
            AiSetupNeed.Repair =>
                "Local AI was removed or is not usable. Super Dev can set it up again. It runs only on this computer, the download is a one-time setup, and no cloud API is used.",
            _ =>
                "Additional local runtime and model files are required. The download is a one-time setup, the files stay on this computer, and no cloud API is used."
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = LoadEstimateAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _run?.Cancel();
        base.OnClosed(e);
    }

    private async Task LoadEstimateAsync()
    {
        if (_need is not (AiSetupNeed.InstallRuntime or AiSetupNeed.Repair))
        {
            if (_need == AiSetupNeed.DownloadModel)
                SizeText.Text = "The model size appears while it downloads.";
            return;
        }

        try
        {
            var estimate = await _setup.DescribeDownloadAsync();
            if (!string.IsNullOrWhiteSpace(estimate))
                SizeText.Text = estimate;
        }
        catch (Exception)
        {
        }
    }

    private async void Primary_Click(object? sender, RoutedEventArgs e)
    {
        if (PrimaryButton.Content as string == "Continue")
        {
            Close(true);
            return;
        }

        await RunAsync();
    }

    private async void Retry_Click(object? sender, RoutedEventArgs e) => await RunAsync();

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _run?.Cancel();
            return;
        }

        Close(false);
    }

    private async Task RunAsync()
    {
        if (_busy)
            return;

        _busy = true;
        _run = new CancellationTokenSource();
        PromptPanel.IsVisible = false;
        ProgressPanel.IsVisible = true;
        ErrorText.IsVisible = false;
        RetryButton.IsVisible = false;
        PrimaryButton.IsVisible = false;
        CloseButton.Content = "Cancel";
        CloseButton.IsEnabled = true;
        ShowProgress(new SetupProgress
        {
            Stage = AiSetupStage.Checking,
            Message = "Checking the local AI environment."
        });

        var progress = new Progress<SetupProgress>(update =>
            Dispatcher.UIThread.Post(() => ShowProgress(update)));
        try
        {
            var result = await _setup.SetupAsync(progress, _run.Token);
            if (result.Succeeded)
            {
                ShowProgress(new SetupProgress
                {
                    Stage = AiSetupStage.Completed,
                    Message = "Local AI is ready.",
                    Fraction = 1
                });
                ErrorText.IsVisible = false;
                CloseButton.Content = "Close";
                PrimaryButton.Content = "Continue";
                PrimaryButton.IsVisible = true;
                return;
            }

            ShowFailure(result);
        }
        catch (Exception)
        {
            ShowFailure(AISetupResult.Fail(
                AiSetupFailureKind.Unexpected,
                "Local AI setup did not finish.",
                "Try again. Manual checklists still work without local AI."));
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowProgress(SetupProgress progress)
    {
        StageText.Text = progress.Stage switch
        {
            AiSetupStage.Checking => "Checking AI environment",
            AiSetupStage.DownloadingRuntime => "Downloading Ollama",
            AiSetupStage.InstallingRuntime => "Installing Ollama",
            AiSetupStage.StartingService => "Starting local AI service",
            AiSetupStage.DownloadingModel => "Downloading Qwen model",
            AiSetupStage.VerifyingModel => "Verifying model",
            AiSetupStage.Initializing => "Initializing AI",
            AiSetupStage.Completed => "Setup completed",
            _ => "Setting up local AI"
        };
        DetailText.Text = progress.Message;
        if (progress.Fraction is double fraction)
        {
            SetupProgress.IsIndeterminate = false;
            SetupProgress.Value = Math.Clamp(fraction, 0, 1) * 100;
            PercentText.Text = Math.Round(SetupProgress.Value) + "%";
        }
        else
        {
            SetupProgress.IsIndeterminate = true;
            PercentText.Text = string.Empty;
        }

        if (progress.CompletedBytes is long completed && progress.TotalBytes is long total && total > 0)
            PercentText.Text = (string.IsNullOrEmpty(PercentText.Text) ? string.Empty : PercentText.Text + " · ")
                + ByteSize.Format(completed) + " of " + ByteSize.Format(total);
        else if (progress.CompletedBytes is long done && done > 0)
            PercentText.Text = (string.IsNullOrEmpty(PercentText.Text) ? string.Empty : PercentText.Text + " · ")
                + ByteSize.Format(done) + " downloaded";
    }

    private void ShowFailure(AISetupResult result)
    {
        ErrorText.IsVisible = true;
        ErrorText.Text = result.Message + " " + result.Recovery;
        CloseButton.Content = "Close";
        RetryButton.IsVisible = !result.Cancelled;
        PrimaryButton.IsVisible = false;
        SetupProgress.IsIndeterminate = false;
    }
}
