using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public sealed class AISetupManager
{
    private readonly ILocalAIService _ai;
    private readonly IOllamaInstallationDetector _detector;
    private readonly IOllamaInstaller _installer;
    private readonly IOllamaProcessManager _processes;
    private readonly IOllamaModelManager _models;
    private readonly IAISetupCache _cache;
    private readonly IOllamaReleaseCatalog _catalog;
    private readonly string _model;
    private readonly TimeSpan _apiWait;
    private readonly TimeSpan _apiPoll;

    public AISetupManager(
        ILocalAIService ai,
        IOllamaInstallationDetector detector,
        IOllamaInstaller installer,
        IOllamaProcessManager processes,
        IOllamaModelManager models,
        IAISetupCache cache,
        IOllamaReleaseCatalog catalog,
        string? modelName = null,
        TimeSpan? apiWait = null,
        TimeSpan? apiPoll = null)
    {
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _model = string.IsNullOrWhiteSpace(modelName) ? AIModelManager.DefaultModel : modelName.Trim();
        _apiWait = apiWait ?? TimeSpan.FromSeconds(90);
        _apiPoll = apiPoll ?? TimeSpan.FromSeconds(1);
    }

    public static AISetupManager Create(OllamaService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var catalog = new GitHubOllamaReleaseCatalog(http);
        return new AISetupManager(
            service,
            new OllamaInstallationDetector(),
            OllamaInstallerFactory.Create(http, catalog, new DriveDiskSpace()),
            new OllamaProcessManager(),
            new OllamaModelManager(service),
            new FileAISetupCache(),
            catalog);
    }

    public string ModelName => _model;

    public async Task<string?> DescribeDownloadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await _catalog.EstimateBytesAsync(cancellationToken).ConfigureAwait(false);
            if (bytes is not long size || size <= 0)
                return null;
            return "The Ollama runtime is about " + ByteSize.Format(size) + ". The model size appears while it downloads.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    public async Task<AIReadiness> CheckAsync(CancellationToken cancellationToken = default)
    {
        var cache = _cache.Read();
        var installation = _detector.Inspect(cache?.ExecutablePath);
        var api = false;
        var modelInstalled = false;
        var modelLoads = false;
        try
        {
            var availability = await _ai.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
            api = availability.Reachability == OllamaReachability.Reachable;
            if (api)
            {
                modelInstalled = await _ai.IsModelAvailableAsync(_model, cancellationToken).ConfigureAwait(false);
                if (modelInstalled)
                    modelLoads = await _models.DescribeAsync(_model, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OllamaRequestException)
        {
            api = false;
        }

        if (installation.RuntimeInstalled && !string.IsNullOrWhiteSpace(installation.ExecutablePath))
        {
            _cache.Write(new AISetupRecord
            {
                HadRuntime = true,
                ExecutablePath = installation.ExecutablePath,
                Model = _model
            });
        }

        return new AIReadiness
        {
            RuntimeInstalled = installation.RuntimeInstalled,
            ExecutableFound = installation.ExecutableFound,
            ProcessRunning = installation.ProcessRunning,
            ApiResponding = api,
            ModelInstalled = modelInstalled,
            ModelLoads = modelLoads,
            ExecutablePath = installation.ExecutablePath,
            Need = Decide(installation.RuntimeInstalled, api, modelInstalled, modelLoads, cache?.HadRuntime == true)
        };
    }

    public async Task<AISetupResult> SetupAsync(IProgress<SetupProgress>? progress, CancellationToken cancellationToken = default)
    {
        try
        {
            Report(progress, AiSetupStage.Checking, "Checking the local AI environment.");
            var cache = _cache.Read();
            var installation = _detector.Inspect(cache?.ExecutablePath);
            var api = await ApiResponds(cancellationToken).ConfigureAwait(false);
            if (!installation.RuntimeInstalled && !api)
            {
                installation = await _installer.InstallAsync(progress, cancellationToken).ConfigureAwait(false);
                _cache.Write(new AISetupRecord
                {
                    HadRuntime = true,
                    ExecutablePath = installation.ExecutablePath,
                    Model = _model
                });
            }

            if (!api)
            {
                Report(progress, AiSetupStage.StartingService, "Starting the local AI service.");
                if (!installation.ProcessRunning)
                    await _processes.StartAsync(installation, cancellationToken).ConfigureAwait(false);
                if (!await WaitForApiAsync(cancellationToken).ConfigureAwait(false))
                {
                    return AISetupResult.Fail(
                        AiSetupFailureKind.ServiceFailed,
                        "Ollama was installed, but the local service did not respond.",
                        "If macOS or Windows asks you to allow Ollama, approve it, then try again.");
                }
            }

            var installed = await _ai.IsModelAvailableAsync(_model, cancellationToken).ConfigureAwait(false);
            if (!installed)
            {
                Report(progress, AiSetupStage.DownloadingModel, "Downloading " + _model + ".");
                var pullProgress = new Progress<ModelDownloadProgress>(update =>
                {
                    double? fraction = update.Total is > 0
                        ? Math.Clamp(update.Completed / (double)update.Total.Value, 0, 1)
                        : null;
                    Report(progress, AiSetupStage.DownloadingModel, string.IsNullOrWhiteSpace(update.Status)
                        ? "Downloading " + _model + "."
                        : update.Status, fraction, update.Completed, update.Total);
                });
                await _models.PullAsync(_model, pullProgress, cancellationToken).ConfigureAwait(false);
            }

            Report(progress, AiSetupStage.VerifyingModel, "Verifying " + _model + ".");
            var described = await _models.DescribeAsync(_model, cancellationToken).ConfigureAwait(false);
            Report(progress, AiSetupStage.Initializing, "Loading " + _model + ".");
            var loaded = described && await _models.LoadAsync(_model, cancellationToken).ConfigureAwait(false);
            if (!described || !loaded)
            {
                return AISetupResult.Fail(
                    AiSetupFailureKind.ModelVerificationFailed,
                    "The model downloaded, but it did not load.",
                    "Try the setup again.");
            }

            Report(progress, AiSetupStage.Completed, "Local AI is ready.", 1);
            return AISetupResult.Ready();
        }
        catch (OperationCanceledException)
        {
            return AISetupResult.Fail(
                AiSetupFailureKind.Cancelled,
                "Setup was cancelled.",
                "Choose Generate with AI when you want to set it up.");
        }
        catch (AISetupException ex)
        {
            return AISetupResult.Fail(ex.Kind, ex.Message, ex.Recovery);
        }
        catch (OllamaRequestException)
        {
            return AISetupResult.Fail(
                AiSetupFailureKind.ModelDownloadFailed,
                "The model download did not finish.",
                "Check the internet connection and try again. A partial download can resume.");
        }
        catch (HttpRequestException)
        {
            return AISetupResult.Fail(
                AiSetupFailureKind.Network,
                "The download was interrupted.",
                "Check the internet connection and try again.");
        }
        catch (IOException)
        {
            return AISetupResult.Fail(
                AiSetupFailureKind.Interrupted,
                "The download was interrupted.",
                "Try the setup again.");
        }
    }

    public static AiSetupNeed Decide(bool runtimeInstalled, bool api, bool modelInstalled, bool modelLoads, bool hadRuntime)
    {
        if (api && modelInstalled && modelLoads)
            return AiSetupNeed.None;
        if (api && modelInstalled)
            return AiSetupNeed.Repair;
        if (api)
            return AiSetupNeed.DownloadModel;
        if (runtimeInstalled)
            return AiSetupNeed.StartRuntime;
        return hadRuntime ? AiSetupNeed.Repair : AiSetupNeed.InstallRuntime;
    }

    private async Task<bool> ApiResponds(CancellationToken cancellationToken)
    {
        try
        {
            var availability = await _ai.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
            return availability.Reachability == OllamaReachability.Reachable;
        }
        catch (OllamaRequestException)
        {
            return false;
        }
    }

    private async Task<bool> WaitForApiAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _apiWait;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ApiResponds(cancellationToken).ConfigureAwait(false))
                return true;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;
            var delay = remaining < _apiPoll ? remaining : _apiPoll;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static void Report(
        IProgress<SetupProgress>? progress,
        AiSetupStage stage,
        string message,
        double? fraction = null,
        long? completed = null,
        long? total = null) =>
        progress?.Report(new SetupProgress
        {
            Stage = stage,
            Message = message,
            Fraction = fraction,
            CompletedBytes = completed,
            TotalBytes = total
        });
}
