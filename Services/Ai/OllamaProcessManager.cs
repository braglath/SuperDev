using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SuperDev.Services.Ai;

public interface IOllamaProcessManager
{
    Task StartAsync(OllamaInstallSnapshot installation, CancellationToken cancellationToken = default);
}

public sealed class OllamaProcessManager : IOllamaProcessManager
{
    public Task StartAsync(OllamaInstallSnapshot installation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.ProcessRunning)
            return Task.CompletedTask;

        if (OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(installation.AppPath))
        {
            Start("open", "-a", installation.AppPath, "--args", "hidden");
            return Task.CompletedTask;
        }

        if (!string.IsNullOrWhiteSpace(installation.ExecutablePath) && File.Exists(installation.ExecutablePath))
        {
            Start(installation.ExecutablePath, "serve");
            return Task.CompletedTask;
        }

        throw new AISetupException(
            Models.AiSetupFailureKind.ServiceFailed,
            "Ollama is installed, but Super Dev could not find the program that starts it.",
            "Install Ollama again, then try setup once more.");
    }

    private static void Start(string fileName, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            Process.Start(start);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            throw new AISetupException(
                Models.AiSetupFailureKind.ServiceFailed,
                "The local AI service did not start.",
                "If the system asks you to allow Ollama, approve it, then try again.");
        }
    }
}

public sealed class AISetupException : Exception
{
    public AISetupException(Models.AiSetupFailureKind kind, string message, string recovery)
        : base(message)
    {
        Kind = kind;
        Recovery = recovery;
    }

    public Models.AiSetupFailureKind Kind { get; }

    public string Recovery { get; }
}
