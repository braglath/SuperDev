using System;

namespace SuperDev.Models;

public enum AiSetupStage
{
    Checking,
    DownloadingRuntime,
    InstallingRuntime,
    StartingService,
    DownloadingModel,
    VerifyingModel,
    Initializing,
    Completed
}

public enum AiSetupNeed
{
    None,
    InstallRuntime,
    StartRuntime,
    DownloadModel,
    Repair
}

public enum AiSetupFailureKind
{
    None,
    UnsupportedPlatform,
    Network,
    Interrupted,
    InsufficientStorage,
    PermissionDenied,
    InstallerFailed,
    ServiceFailed,
    ApiUnavailable,
    ModelDownloadFailed,
    ModelVerificationFailed,
    Cancelled,
    Unexpected
}

public sealed class AIReadiness
{
    public bool RuntimeInstalled { get; init; }

    public bool ExecutableFound { get; init; }

    public bool ProcessRunning { get; init; }

    public bool ApiResponding { get; init; }

    public bool ModelInstalled { get; init; }

    public bool ModelLoads { get; init; }

    public string? ExecutablePath { get; init; }

    public AiSetupNeed Need { get; init; }

    public bool IsReady => Need == AiSetupNeed.None && ApiResponding && ModelInstalled && ModelLoads;
}

public sealed class SetupProgress
{
    public AiSetupStage Stage { get; init; }

    public string Message { get; init; } = string.Empty;

    public double? Fraction { get; init; }

    public long? CompletedBytes { get; init; }

    public long? TotalBytes { get; init; }
}

public readonly record struct ModelDownloadProgress(long Completed, long? Total, string Status, bool Succeeded, bool Failed);

public sealed class AISetupResult
{
    public bool Succeeded { get; init; }

    public bool Cancelled { get; init; }

    public AiSetupFailureKind Failure { get; init; }

    public string Message { get; init; } = string.Empty;

    public string Recovery { get; init; } = string.Empty;

    public static AISetupResult Ready() => new()
    {
        Succeeded = true,
        Message = "Local AI is ready."
    };

    public static AISetupResult Fail(AiSetupFailureKind kind, string message, string recovery) => new()
    {
        Failure = kind,
        Cancelled = kind == AiSetupFailureKind.Cancelled,
        Message = message,
        Recovery = recovery
    };
}

public sealed class AISetupRecord
{
    public bool HadRuntime { get; set; }

    public string? ExecutablePath { get; set; }

    public string Model { get; set; } = "qwen3.5:0.8b";
}

public static class ByteSize
{
    public static string Format(long bytes)
    {
        if (bytes < 0)
            bytes = 0;
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? bytes + " B" : size.ToString("0.#") + " " + units[unit];
    }
}
