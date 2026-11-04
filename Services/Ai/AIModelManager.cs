using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public sealed class AIModelManager
{
    public const string DefaultModel = "qwen3.5:0.8b";

    private readonly ILocalAIService _service;
    private readonly Func<bool> _installationProbe;
    private readonly string _model;

    public AIModelManager(ILocalAIService service, Func<bool>? installationProbe = null, string? modelName = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _installationProbe = installationProbe ?? OllamaInstallProbe.IsInstalled;
        _model = string.IsNullOrWhiteSpace(modelName) ? DefaultModel : modelName.Trim();
    }

    public string ModelName => _model;

    public async Task<ModelStatus> DetectAsync(CancellationToken cancellationToken = default)
    {
        var availability = await _service.CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (availability.Reachability != OllamaReachability.Reachable)
            return Unreachable(availability.Reachability);

        try
        {
            var installed = await _service.IsModelAvailableAsync(_model, cancellationToken).ConfigureAwait(false);
            return installed ? Ready() : Missing();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OllamaRequestException)
        {
            return Unavailable();
        }
    }

    private ModelStatus Ready() => new()
    {
        Status = OllamaStatus.Ready,
        ModelName = _model,
        ConnectionText = "Ollama connected",
        ModelStateText = "Model installed",
        Guidance = "Checklist generation stays on this computer."
    };

    private ModelStatus Missing() => new()
    {
        Status = OllamaStatus.ModelMissing,
        ModelName = _model,
        ConnectionText = "Ollama connected",
        ModelStateText = "Model missing",
        Guidance = AiMessages.For(AiFailureKind.ModelMissing, _model)
    };

    private ModelStatus Unavailable() => new()
    {
        Status = OllamaStatus.Unavailable,
        ModelName = _model,
        ConnectionText = "Ollama disconnected",
        ModelStateText = "Not checked",
        Guidance = AiMessages.For(AiFailureKind.Unexpected, _model)
    };

    private ModelStatus Unreachable(OllamaReachability reachability)
    {
        var installed = false;
        try
        {
            installed = _installationProbe();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            installed = false;
        }

        if (!installed)
        {
            return new ModelStatus
            {
                Status = OllamaStatus.NotInstalled,
                ModelName = _model,
                ConnectionText = "Ollama disconnected",
                ModelStateText = "Not checked",
                Guidance = AiMessages.For(AiFailureKind.NotInstalled, _model)
            };
        }

        var timedOut = reachability == OllamaReachability.TimedOut;
        return new ModelStatus
        {
            Status = OllamaStatus.NotRunning,
            ModelName = _model,
            ConnectionText = timedOut ? "Ollama is not responding" : "Ollama disconnected",
            ModelStateText = "Not checked",
            Guidance = timedOut
                ? "The local AI service did not respond. Choose Generate to start it again."
                : AiMessages.For(AiFailureKind.NotRunning, _model)
        };
    }
}

public static class OllamaInstallProbe
{
    public static bool IsInstalled()
    {
        if (ExistsOnPath("ollama") || ExistsOnPath("ollama.exe"))
            return true;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            "/opt/homebrew/bin/ollama",
            "/usr/local/bin/ollama",
            "/usr/bin/ollama",
            "/Applications/Ollama.app",
            Path.Combine(home, "Applications", "Ollama.app"),
            Path.Combine(local, "Programs", "Ollama", "ollama.exe"),
            Path.Combine(programFiles, "Ollama", "ollama.exe")
        ];

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    private static bool ExistsOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return false;

        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(folder.Trim(), fileName)))
                    return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return false;
    }
}
