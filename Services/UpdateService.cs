using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SuperDev.Services;

public enum UpdatePackageKind
{
    None,
    MacApp,
    WindowsZip,
    WindowsMsix
}

public sealed class UpdateCheckResult
{
    public string CurrentText { get; init; } = "";
    public string? LatestText { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? DownloadUrl { get; init; }
    public string? AssetName { get; init; }
    public UpdatePackageKind Kind { get; init; }
    public bool IsNewer { get; init; }
    public bool CanInstall { get; init; }
    public string? Note { get; init; }
    public string? Error { get; init; }

    public static UpdateCheckResult Failed(string error) => new()
    {
        CurrentText = UpdateService.CurrentText,
        Error = error
    };
}

public sealed class PreparedUpdate
{
    private Action? _apply;
    private Action? _cancel;

    private PreparedUpdate(string? error, Action? apply, Action? cancel)
    {
        Error = error;
        _apply = apply;
        _cancel = cancel;
    }

    public string? Error { get; }

    public static PreparedUpdate Failed(string error) => new(error, null, null);

    public static PreparedUpdate Ready(Action apply, Action cancel) => new(null, apply, cancel);

    public void Apply()
    {
        var apply = _apply ?? throw new InvalidOperationException("Update isn't ready.");
        apply();
        _apply = null;
        _cancel = null;
    }

    public void Cancel()
    {
        var cancel = _cancel;
        _apply = null;
        _cancel = null;
        cancel?.Invoke();
    }
}

public static class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/braglath/SuperDev/releases/latest";
    private const int NotPackaged = 15700;

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static UpdateSettings _settings = Load();

    public static string CurrentText
    {
        get
        {
            var version = CurrentVersion();
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static bool CheckAutomatically
    {
        get => _settings.CheckAutomatically;
        set
        {
            if (_settings.CheckAutomatically == value && (value || !_settings.InstallAutomatically))
                return;

            _settings.CheckAutomatically = value;
            if (!value)
                _settings.InstallAutomatically = false;
            Save();
        }
    }

    public static bool InstallAutomatically
    {
        get => _settings.InstallAutomatically;
        set
        {
            if (_settings.InstallAutomatically == value && (!value || _settings.CheckAutomatically))
                return;

            _settings.InstallAutomatically = value;
            if (value)
                _settings.CheckAutomatically = true;
            Save();
        }
    }

    public static bool CanReplaceThisInstall
    {
        get
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
                return false;

            if (OperatingSystem.IsMacOS())
                return FindAppBundle(path) is not null;

            if (OperatingSystem.IsWindows())
                return !IsDevelopmentBuild(path);

            return false;
        }
    }

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.UserAgent.ParseAdd("SuperDev");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Failed("Couldn't check for updates.");

            var body = await response.Content.ReadAsStringAsync(linked.Token);
            var release = JsonSerializer.Deserialize<GitHubRelease>(body);
            if (release?.TagName is null || !TryParseTag(release.TagName, out var latest))
                return UpdateCheckResult.Failed("Couldn't read the latest release.");

            var current = CurrentVersion();
            var newer = latest > current;
            var kind = PackageKind();
            var asset = FindAsset(release.Assets, kind);
            var canInstall = false;
            string? note = null;
            if (newer)
            {
                if (asset?.BrowserDownloadUrl is null)
                    note = "No download is published for this computer.";
                else if (!CanReplaceThisInstall)
                    note = IsDevelopmentBuild(Environment.ProcessPath)
                        ? "This development build can't replace itself. Download the release to install it."
                        : "This copy can't replace itself. Download the release to install it.";
                else
                    canInstall = true;
            }

            return new UpdateCheckResult
            {
                CurrentText = CurrentText,
                LatestText = $"{latest.Major}.{latest.Minor}.{latest.Build}",
                ReleaseUrl = release.HtmlUrl,
                DownloadUrl = asset?.BrowserDownloadUrl,
                AssetName = asset?.Name,
                Kind = kind,
                IsNewer = newer,
                CanInstall = canInstall,
                Note = note
            };
        }
        catch (OperationCanceledException)
        {
            return UpdateCheckResult.Failed("The update check timed out.");
        }
        catch (HttpRequestException)
        {
            return UpdateCheckResult.Failed("Couldn't reach GitHub to check for updates.");
        }
        catch (Exception)
        {
            return UpdateCheckResult.Failed("Couldn't check for updates.");
        }
    }

    public static async Task<PreparedUpdate> PrepareInstallAsync(
        UpdateCheckResult update,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl) || update.Kind == UpdatePackageKind.None)
            return PreparedUpdate.Failed("No download is published for this computer.");

        var work = Path.Combine(Path.GetTempPath(), "Super Dev Update", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var fileName = string.IsNullOrWhiteSpace(update.AssetName)
                ? "SuperDev-update" + ExtensionFor(update.Kind)
                : Path.GetFileName(update.AssetName);
            var packagePath = Path.Combine(work, fileName);
            await DownloadAsync(update.DownloadUrl, packagePath, progress, cancellationToken);
            if (!update.CanInstall)
            {
                progress?.Report("Downloaded.");
                return PreparedUpdate.Ready(
                    () => OpenDownloaded(packagePath),
                    () => DeleteQuiet(work));
            }

            progress?.Report("Preparing the update…");

            return update.Kind switch
            {
                UpdatePackageKind.MacApp => await PrepareMacAsync(packagePath, work, cancellationToken),
                UpdatePackageKind.WindowsZip => PrepareWindowsZip(packagePath, work),
                UpdatePackageKind.WindowsMsix => PrepareWindowsMsix(packagePath, work),
                _ => throw new InvalidOperationException("This copy can't install updates.")
            };
        }
        catch (OperationCanceledException)
        {
            DeleteQuiet(work);
            return PreparedUpdate.Failed("The download was canceled.");
        }
        catch (HttpRequestException)
        {
            DeleteQuiet(work);
            return PreparedUpdate.Failed("Couldn't download the update.");
        }
        catch (Exception ex)
        {
            DeleteQuiet(work);
            var message = ex is InvalidOperationException or IOException or UnauthorizedAccessException or HttpRequestException
                ? ex.Message
                : "Couldn't install the update.";
            if (string.IsNullOrWhiteSpace(message))
                message = "Couldn't install the update.";
            return PreparedUpdate.Failed(message);
        }
    }

    private static async Task DownloadAsync(
        string url,
        string destination,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("SuperDev");
        request.Headers.Accept.ParseAdd("application/octet-stream");

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Couldn't download the update.");

        var total = response.Content.Headers.ContentLength;
        progress?.Report(total is > 0 ? "Downloading 0%…" : "Downloading…");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long received = 0;
        var lastPercent = -1;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            received += count;
            if (total is not > 0)
                continue;

            var percent = (int)Math.Clamp(received * 100 / total.Value, 0, 100);
            if (percent == lastPercent)
                continue;

            lastPercent = percent;
            progress?.Report($"Downloading {percent}%…");
        }
    }

    private static async Task<PreparedUpdate> PrepareMacAsync(
        string dmgPath,
        string work,
        CancellationToken cancellationToken)
    {
        var bundle = FindAppBundle(Environment.ProcessPath);
        if (bundle is null)
            throw new InvalidOperationException("This development build can't install updates.");

        var parent = Path.GetDirectoryName(bundle);
        if (string.IsNullOrEmpty(parent))
            throw new InvalidOperationException("This copy can't install updates.");

        EnsureWritable(parent);

        var mount = Path.Combine(work, "mount");
        Directory.CreateDirectory(mount);
        await RunAsync(
            "/usr/bin/hdiutil",
            ["attach", dmgPath, "-mountpoint", mount, "-nobrowse", "-readonly", "-noverify"],
            cancellationToken);

        try
        {
            var source = Path.Combine(mount, "Super Dev.app");
            if (!Directory.Exists(source))
                throw new InvalidOperationException("The downloaded disk image doesn't contain Super Dev.");

            var staged = Path.Combine(work, "Super Dev.app");
            await RunAsync("/usr/bin/ditto", [source, staged], cancellationToken);
            try
            {
                await RunAsync("/usr/bin/xattr", ["-dr", "com.apple.quarantine", staged], cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // The staged copy can still be swapped if quarantine is already clear.
            }
        }
        finally
        {
            try
            {
                await RunAsync("/usr/bin/hdiutil", ["detach", mount], CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Attach may have failed before the image was mounted.
            }
        }

        var script = Path.Combine(work, "update.sh");
        await File.WriteAllTextAsync(script, MacScript, cancellationToken);
        return PreparedUpdate.Ready(
            () => StartDetachedMac(script, Environment.ProcessId.ToString(), bundle, work),
            () => DeleteQuiet(work));
    }

    private static PreparedUpdate PrepareWindowsZip(string zipPath, string work)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            throw new InvalidOperationException("This copy can't install updates.");

        var installDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(installDir))
            throw new InvalidOperationException("This copy can't install updates.");

        EnsureWritable(installDir);
        var extracted = Path.Combine(work, "package");
        Directory.CreateDirectory(extracted);
        ExtractZip(zipPath, extracted);
        var stagedExe = Directory.EnumerateFiles(extracted, "Super Dev.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (stagedExe is null)
            throw new InvalidOperationException("The downloaded package doesn't contain Super Dev.");

        var source = Path.GetDirectoryName(stagedExe)!;
        var script = Path.Combine(work, "update.ps1");
        File.WriteAllText(script, WindowsScript(
            Environment.ProcessId,
            source,
            installDir,
            exePath,
            work));
        return PreparedUpdate.Ready(
            () => StartWindowsHelper(script),
            () => DeleteQuiet(work));
    }

    private static PreparedUpdate PrepareWindowsMsix(string msixPath, string work)
    {
        if (!File.Exists(msixPath))
            throw new InvalidOperationException("Couldn't download the update.");

        return PreparedUpdate.Ready(
            () =>
            {
                Process.Start(new ProcessStartInfo(msixPath) { UseShellExecute = true });
            },
            () => DeleteQuiet(work));
    }

    private static void OpenDownloaded(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void StartDetachedMac(string script, string pid, string bundle, string work)
    {
        var start = new ProcessStartInfo("/bin/bash")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("nohup /bin/bash \"$0\" \"$@\" >/dev/null 2>&1 &");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(pid);
        start.ArgumentList.Add(bundle);
        start.ArgumentList.Add(work);
        Process.Start(start);
    }

    private static void StartWindowsHelper(string scriptPath)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-WindowStyle");
        start.ArgumentList.Add("Hidden");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(scriptPath);
        Process.Start(start);
    }

    private static async Task RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {Path.GetFileName(fileName)}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        _ = await stdoutTask;
        var error = (await stderrTask).Trim();
        if (process.ExitCode == 0)
            return;

        var detail = FirstLine(error);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
            ? $"Couldn't prepare the update ({Path.GetFileName(fileName)})."
            : detail);
    }

    private static void ExtractZip(string zipPath, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The downloaded package isn't valid.");

            var folder = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void EnsureWritable(string directory)
    {
        var probe = Path.Combine(directory, ".superdev-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Super Dev can't replace this installation. Choose a folder you can write to, or install the release yourself.");
        }
    }

    private static UpdatePackageKind PackageKind()
    {
        if (OperatingSystem.IsMacOS())
            return UpdatePackageKind.MacApp;

        if (!OperatingSystem.IsWindows())
            return UpdatePackageKind.None;

        return IsWindowsAppPackage() ? UpdatePackageKind.WindowsMsix : UpdatePackageKind.WindowsZip;
    }

    private static GitHubAsset? FindAsset(List<GitHubAsset>? assets, UpdatePackageKind kind)
    {
        if (assets is null || kind == UpdatePackageKind.None)
            return null;

        var suffix = kind switch
        {
            UpdatePackageKind.MacApp => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "-osx-arm64.dmg"
                : "-osx-x64.dmg",
            UpdatePackageKind.WindowsMsix => "-win-x64.msix",
            UpdatePackageKind.WindowsZip => "-win-x64.zip",
            _ => ""
        };

        return assets.FirstOrDefault(asset =>
            asset.Name is not null &&
            asset.BrowserDownloadUrl is not null &&
            asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtensionFor(UpdatePackageKind kind) => kind switch
    {
        UpdatePackageKind.MacApp => ".dmg",
        UpdatePackageKind.WindowsZip => ".zip",
        UpdatePackageKind.WindowsMsix => ".msix",
        _ => ".bin"
    };

    private static Version CurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    private static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0)
            text = text[..cut];

        if (!Version.TryParse(text, out var parsed))
            return false;

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    private static bool IsDevelopmentBuild(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return true;

        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/Debug/", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("/bin/Release/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindAppBundle(string? processPath)
    {
        var dir = Path.GetDirectoryName(processPath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return dir;

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsAppPackage()
    {
        try
        {
            var length = 0;
            var result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
            return result != NotPackaged;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string FirstLine(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var line = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        line = line.Trim();
        return line.Length <= 180 ? line : line[..180];
    }

    private static string WindowsScript(int processId, string source, string target, string exe, string work)
    {
        return $$"""
            $ErrorActionPreference = 'Stop'
            $appId = {{processId}}
            $source = '{{PsQuote(source)}}'
            $target = '{{PsQuote(target)}}'
            $exe = '{{PsQuote(exe)}}'
            $work = '{{PsQuote(work)}}'
            while (Get-Process -Id $appId -ErrorAction SilentlyContinue) {
              Start-Sleep -Milliseconds 200
            }
            Start-Sleep -Milliseconds 500
            Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
            Start-Process -FilePath $exe
            Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
            """;
    }

    private static string PsQuote(string value) => value.Replace("'", "''");

    private static void DeleteQuiet(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // A leftover download must not block the app.
        }
    }

    private const string MacScript = """
        #!/bin/bash
        set -u
        pid="$1"
        dest="$2"
        root="$3"
        while kill -0 "$pid" 2>/dev/null; do
          sleep 0.2
        done
        sleep 0.5
        staged="$root/Super Dev.app"
        previous="${dest}.previous"
        rm -rf "$previous"
        if ! /bin/mv "$dest" "$previous"; then
          exit 1
        fi
        if /usr/bin/ditto "$staged" "$dest"; then
          /usr/bin/xattr -dr com.apple.quarantine "$dest" || true
          /bin/rm -rf "$previous" "$root"
          /usr/bin/open "$dest"
          exit 0
        fi
        /bin/rm -rf "$dest"
        /bin/mv "$previous" "$dest"
        /usr/bin/open "$dest"
        exit 1
        """;

    private static UpdateSettings Load()
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path))
                return new UpdateSettings();

            return JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(path), Json)
                   ?? new UpdateSettings();
        }
        catch (Exception)
        {
            return new UpdateSettings();
        }
    }

    private static void Save()
    {
        try
        {
            var path = SettingsPath();
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_settings, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // A failed preference write must not block the rest of the app.
        }
    }

    private static string SettingsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Super Dev",
            "updates.json");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, IntPtr packageFullName);

    private sealed class UpdateSettings
    {
        public bool CheckAutomatically { get; set; }
        public bool InstallAutomatically { get; set; }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
