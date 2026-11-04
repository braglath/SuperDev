using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public interface IDiskSpace
{
    long Available(string path);
}

public sealed class DriveDiskSpace : IDiskSpace
{
    public long Available(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
                return long.MaxValue;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return long.MaxValue;
        }
    }
}

public interface IOllamaInstaller
{
    bool IsSupported { get; }

    Task<OllamaInstallSnapshot> InstallAsync(IProgress<SetupProgress>? progress, CancellationToken cancellationToken = default);
}

public static class OllamaInstallerFactory
{
    public static IOllamaInstaller Create(HttpClient http, IOllamaReleaseCatalog catalog, IDiskSpace disk)
    {
        if (OperatingSystem.IsMacOS())
            return new MacOllamaInstaller(http, catalog, disk);
        if (OperatingSystem.IsWindows())
            return new WindowsOllamaInstaller(http, catalog, disk);
        return new UnsupportedOllamaInstaller();
    }
}

public sealed class UnsupportedOllamaInstaller : IOllamaInstaller
{
    public bool IsSupported => false;

    public Task<OllamaInstallSnapshot> InstallAsync(IProgress<SetupProgress>? progress, CancellationToken cancellationToken = default) =>
        throw new AISetupException(
            AiSetupFailureKind.UnsupportedPlatform,
            "Automatic local AI setup is available on macOS and Windows.",
            "Manual checklists keep working without local AI.");
}

file sealed class MacOllamaInstaller : RuntimeInstaller
{
    public MacOllamaInstaller(HttpClient http, IOllamaReleaseCatalog catalog, IDiskSpace disk)
        : base(http, catalog, disk)
    {
    }

    public override bool IsSupported => OperatingSystem.IsMacOS();

    protected override async Task<OllamaInstallSnapshot> InstallVerifiedAsync(
        string archive,
        IProgress<SetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, AiSetupStage.InstallingRuntime, "Installing Ollama.", null, null, null);
        var temp = Path.Combine(Path.GetTempPath(), "superdev-ollama-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var extract = Path.Combine(temp, "extract");
            Directory.CreateDirectory(extract);
            await RunAsync("/usr/bin/ditto", ["-x", "-k", archive, extract], cancellationToken).ConfigureAwait(false);
            var source = Path.Combine(extract, "Ollama.app");
            if (!Directory.Exists(source))
                throw InstallFailed("The Ollama download did not contain the app.");

            var destination = ChooseDestination();
            if (!Directory.Exists(destination))
                destination = await CopyAppAsync(source, destination, cancellationToken).ConfigureAwait(false);

            var executable = Path.Combine(destination, "Contents", "Resources", "ollama");
            return new OllamaInstallSnapshot
            {
                RuntimeInstalled = true,
                ExecutableFound = File.Exists(executable),
                ProcessRunning = false,
                ExecutablePath = File.Exists(executable) ? executable : null,
                AppPath = destination
            };
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static string ChooseDestination()
    {
        const string system = "/Applications/Ollama.app";
        if (Directory.Exists(system) || CanWrite("/Applications"))
            return system;

        var userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications");
        Directory.CreateDirectory(userRoot);
        return Path.Combine(userRoot, "Ollama.app");
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".superdev-write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<string> CopyAppAsync(string source, string destination, CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync("/usr/bin/ditto", [source, destination], cancellationToken).ConfigureAwait(false);
            return destination;
        }
        catch (AISetupException) when (destination.StartsWith("/Applications", StringComparison.Ordinal))
        {
            var userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications");
            Directory.CreateDirectory(userRoot);
            var userApp = Path.Combine(userRoot, "Ollama.app");
            await RunAsync("/usr/bin/ditto", [source, userApp], cancellationToken).ConfigureAwait(false);
            return userApp;
        }
    }
}

file sealed class WindowsOllamaInstaller : RuntimeInstaller
{
    public WindowsOllamaInstaller(HttpClient http, IOllamaReleaseCatalog catalog, IDiskSpace disk)
        : base(http, catalog, disk)
    {
    }

    public override bool IsSupported => OperatingSystem.IsWindows();

    protected override async Task<OllamaInstallSnapshot> InstallVerifiedAsync(
        string archive,
        IProgress<SetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, AiSetupStage.InstallingRuntime,
            "Windows will ask you to approve the Ollama installer. Finish that window and Super Dev will continue.",
            null, null, null);

        if (archive.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            await RunInstallerAsync(archive, cancellationToken).ConfigureAwait(false);
            var found = new OllamaInstallationDetector().Inspect(cachedPath: null);
            if (!found.RuntimeInstalled)
                throw InstallFailed("The Ollama installer finished without installing Ollama.");
            return found;
        }

        return ExtractZip(archive);
    }

    private static async Task RunInstallerAsync(string setup, CancellationToken cancellationToken)
    {
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = setup,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            throw new AISetupException(
                AiSetupFailureKind.PermissionDenied,
                "Windows did not start the Ollama installer.",
                "Approve the installation prompt, then try again.");
        }

        if (process is null)
            throw InstallFailed("Windows did not start the Ollama installer.");

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static OllamaInstallSnapshot ExtractZip(string archive)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Ollama");
        Directory.CreateDirectory(root);
        using (var zip = ZipFile.OpenRead(archive))
        {
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;
                var target = OfficialOllamaRelease.SafeEntryPath(root, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        var executable = FindExe(root);
        if (executable is null)
            throw InstallFailed("The Windows Ollama archive did not contain ollama.exe.");

        return new OllamaInstallSnapshot
        {
            RuntimeInstalled = true,
            ExecutableFound = true,
            ProcessRunning = false,
            ExecutablePath = executable,
            AppPath = null
        };
    }

    private static string? FindExe(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "ollama.exe", SearchOption.AllDirectories))
            return file;
        return null;
    }
}

public abstract class RuntimeInstaller : IOllamaInstaller
{
    private readonly HttpClient _http;
    private readonly IOllamaReleaseCatalog _catalog;
    private readonly IDiskSpace _disk;

    protected RuntimeInstaller(HttpClient http, IOllamaReleaseCatalog catalog, IDiskSpace disk)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _disk = disk ?? throw new ArgumentNullException(nameof(disk));
    }

    public abstract bool IsSupported { get; }

    public async Task<OllamaInstallSnapshot> InstallAsync(
        IProgress<SetupProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            throw new AISetupException(
                AiSetupFailureKind.UnsupportedPlatform,
                "Automatic local AI setup is available on macOS and Windows.",
                "Manual checklists keep working without local AI.");

        var asset = await _catalog.FindAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new AISetupException(
                AiSetupFailureKind.Network,
                "Super Dev could not find the official Ollama download.",
                "Check the internet connection and try again.");

        if (!OfficialOllamaRelease.IsAllowedDownload(asset.Url))
            throw InstallFailed("The Ollama download address was not an official source.");

        var needed = Math.Max(asset.Size, 0) + (2L * 1024 * 1024 * 1024);
        var tempRoot = Path.GetTempPath();
        if (_disk.Available(tempRoot) < needed)
            throw OutOfSpace(needed);

        var archive = Path.Combine(tempRoot, "superdev-" + asset.Name);
        try
        {
            await DownloadAsync(asset, archive, progress, cancellationToken).ConfigureAwait(false);
            if (!OfficialOllamaRelease.HashEquals(archive, asset.Sha256))
                throw InstallFailed("The Ollama download did not match its published checksum.");

            return await InstallVerifiedAsync(archive, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AISetupException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new AISetupException(
                AiSetupFailureKind.Network,
                "The Ollama download was interrupted.",
                "Check the internet connection and try again.");
        }
        catch (IOException)
        {
            throw new AISetupException(
                AiSetupFailureKind.Interrupted,
                "The Ollama download was interrupted.",
                "Try the setup again. A finished download is not repeated.");
        }
        finally
        {
            TryDelete(archive);
        }
    }

    protected abstract Task<OllamaInstallSnapshot> InstallVerifiedAsync(
        string archive,
        IProgress<SetupProgress>? progress,
        CancellationToken cancellationToken);

    private async Task DownloadAsync(
        OllamaReleaseAsset asset,
        string destination,
        IProgress<SetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AISetupException(
                AiSetupFailureKind.Network,
                "The official Ollama download did not start.",
                "Check the internet connection and try again.");

        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is not null && !OfficialOllamaRelease.IsAllowedDownload(finalUri))
            throw InstallFailed("The Ollama download was redirected away from the official source.");

        var total = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : (long?)null);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[1024 * 128];
        long completed = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            completed += read;
            var fraction = total is > 0 ? Math.Clamp(completed / (double)total.Value, 0, 1) : (double?)null;
            Report(progress, AiSetupStage.DownloadingRuntime, "Downloading Ollama.", fraction, completed, total);
        }
    }

    protected static void Report(
        IProgress<SetupProgress>? progress,
        AiSetupStage stage,
        string message,
        double? fraction,
        long? completed,
        long? total) =>
        progress?.Report(new SetupProgress
        {
            Stage = stage,
            Message = message,
            Fraction = fraction,
            CompletedBytes = completed,
            TotalBytes = total
        });

    protected static async Task RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
            throw InstallFailed("Ollama could not be unpacked.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw InstallFailed("Ollama could not be unpacked.");
    }

    protected static AISetupException InstallFailed(string message) => new(
        AiSetupFailureKind.InstallerFailed,
        message,
        "Try the setup again.");

    protected static AISetupException OutOfSpace(long needed) => new(
        AiSetupFailureKind.InsufficientStorage,
        "This computer needs about " + ByteSize.Format(needed) + " free for local AI.",
        "Free up disk space and try again.");

    protected static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
