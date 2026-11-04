using System;
using System.Diagnostics;
using System.IO;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public interface IOllamaInstallationDetector
{
    OllamaInstallSnapshot Inspect(string? cachedPath);
}

public sealed class OllamaInstallSnapshot
{
    public bool RuntimeInstalled { get; init; }

    public bool ExecutableFound { get; init; }

    public bool ProcessRunning { get; init; }

    public string? ExecutablePath { get; init; }

    public string? AppPath { get; init; }
}

public sealed class OllamaInstallationDetector : IOllamaInstallationDetector
{
    public OllamaInstallSnapshot Inspect(string? cachedPath)
    {
        var app = FindApp();
        var executable = FindExecutable(cachedPath, app);
        var running = IsProcessRunning();
        return new OllamaInstallSnapshot
        {
            RuntimeInstalled = app is not null || executable is not null,
            ExecutableFound = executable is not null,
            ProcessRunning = running,
            ExecutablePath = executable,
            AppPath = app
        };
    }

    private static string? FindApp()
    {
        if (!OperatingSystem.IsMacOS())
            return null;

        string[] apps =
        [
            "/Applications/Ollama.app",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Ollama.app")
        ];
        foreach (var app in apps)
        {
            if (Directory.Exists(app))
                return app;
        }

        return null;
    }

    private static string? FindExecutable(string? cachedPath, string? app)
    {
        if (IsFile(cachedPath))
            return cachedPath;

        if (app is not null)
        {
            var bundled = Path.Combine(app, "Contents", "Resources", "ollama");
            if (IsFile(bundled))
                return bundled;
            var macOs = Path.Combine(app, "Contents", "MacOS", "Ollama");
            if (IsFile(macOs))
                return macOs;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            "/opt/homebrew/bin/ollama",
            "/usr/local/bin/ollama",
            "/usr/bin/ollama",
            Path.Combine(local, "Programs", "Ollama", "ollama.exe"),
            Path.Combine(programFiles, "Ollama", "ollama.exe"),
            Path.Combine(home, "Applications", "Ollama.app", "Contents", "Resources", "ollama")
        ];
        foreach (var candidate in candidates)
        {
            if (IsFile(candidate))
                return candidate;
        }

        return FindOnPath(OperatingSystem.IsWindows() ? "ollama.exe" : "ollama");
    }

    private static bool IsProcessRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;
                    if (name.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("ollama", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("ollama app", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }

        return false;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder.Trim(), fileName);
            if (IsFile(candidate))
                return candidate;
        }

        return null;
    }

    private static bool IsFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
