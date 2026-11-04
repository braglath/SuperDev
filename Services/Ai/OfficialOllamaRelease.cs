using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SuperDev.Services.Ai;

public sealed class OllamaReleaseAsset
{
    public required string Name { get; init; }

    public required Uri Url { get; init; }

    public long Size { get; init; }

    public required string Sha256 { get; init; }
}

public static class OfficialOllamaRelease
{
    public const string MacZip = "Ollama-darwin.zip";

    public const string WindowsSetup = "OllamaSetup.exe";

    public const string WindowsArmZip = "ollama-windows-arm64.zip";

    public static string AssetNameForCurrentPlatform()
    {
        if (OperatingSystem.IsMacOS())
            return MacZip;
        if (OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
            return WindowsArmZip;
        if (OperatingSystem.IsWindows())
            return WindowsSetup;
        return string.Empty;
    }

    public static OllamaReleaseAsset? Select(string releaseJson, string checksumText, string assetName)
    {
        if (string.IsNullOrWhiteSpace(releaseJson) || string.IsNullOrWhiteSpace(assetName))
            return null;

        var checksum = FindChecksum(checksumText, assetName);
        if (checksum is null)
            return null;

        try
        {
            using var document = JsonDocument.Parse(releaseJson);
            if (!document.RootElement.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                if (!string.Equals(name, assetName, StringComparison.Ordinal))
                    continue;

                var urlText = asset.TryGetProperty("browser_download_url", out var urlValue) ? urlValue.GetString() : null;
                if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                    return null;

                var size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes)
                    ? bytes
                    : 0;
                return new OllamaReleaseAsset
                {
                    Name = assetName,
                    Url = url,
                    Size = size,
                    Sha256 = checksum
                };
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static long? AssetSize(string releaseJson, string assetName)
    {
        if (string.IsNullOrWhiteSpace(releaseJson) || string.IsNullOrWhiteSpace(assetName))
            return null;

        try
        {
            using var document = JsonDocument.Parse(releaseJson);
            if (!document.RootElement.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                if (!string.Equals(name, assetName, StringComparison.Ordinal))
                    continue;
                return asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) && bytes > 0
                    ? bytes
                    : null;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static string? FindChecksum(string? checksumText, string assetName)
    {
        if (string.IsNullOrWhiteSpace(checksumText) || string.IsNullOrWhiteSpace(assetName))
            return null;

        foreach (var raw in checksumText.Split('\n', '\r'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            var file = parts[^1].Trim().TrimStart('*');
            if (!file.Equals(assetName, StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith("/" + assetName, StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith("\\" + assetName, StringComparison.OrdinalIgnoreCase))
                continue;

            var hash = parts[0].Trim().ToLowerInvariant();
            if (hash.Length == 64 && IsHex(hash))
                return hash;
        }

        return null;
    }

    public static bool HashEquals(string path, string expectedHex)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return actual.Equals(expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedDownload(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            return false;

        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("ollama.com", StringComparison.OrdinalIgnoreCase);
    }

    public static string SafeEntryPath(string root, string entryName)
    {
        var fullRoot = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(
            fullRoot,
            entryName.Replace('\\', '/').TrimStart('/')));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.Ordinal) && !combined.Equals(fullRoot, StringComparison.Ordinal))
            throw new InvalidOperationException("The Ollama archive contained an unsafe path.");
        return combined;
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            var hex = character is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!hex)
                return false;
        }

        return true;
    }
}

public interface IOllamaReleaseCatalog
{
    Task<long?> EstimateBytesAsync(CancellationToken cancellationToken = default);

    Task<OllamaReleaseAsset?> FindAsync(CancellationToken cancellationToken = default);
}

public sealed class GitHubOllamaReleaseCatalog : IOllamaReleaseCatalog
{
    private readonly HttpClient _http;

    public GitHubOllamaReleaseCatalog(HttpClient http) =>
        _http = http ?? throw new ArgumentNullException(nameof(http));

    public Task<long?> EstimateBytesAsync(CancellationToken cancellationToken = default) =>
        ReadReleaseAsync(
            cancellationToken,
            json => OfficialOllamaRelease.AssetSize(json, OfficialOllamaRelease.AssetNameForCurrentPlatform()));

    public async Task<OllamaReleaseAsset?> FindAsync(CancellationToken cancellationToken = default)
    {
        var assetName = OfficialOllamaRelease.AssetNameForCurrentPlatform();
        if (assetName.Length == 0)
            return null;

        var releaseJson = await ReadReleaseAsync(cancellationToken, json => json).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(releaseJson))
            return null;

        var checksumUrl = ChecksumUrl(releaseJson);
        if (checksumUrl is null || !OfficialOllamaRelease.IsAllowedDownload(checksumUrl))
            return null;

        using var checksumResponse = await _http.GetAsync(checksumUrl, cancellationToken).ConfigureAwait(false);
        if (!checksumResponse.IsSuccessStatusCode)
            return null;

        var checksumText = await checksumResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return OfficialOllamaRelease.Select(releaseJson, checksumText, assetName);
    }

    private async Task<T?> ReadReleaseAsync<T>(
        CancellationToken cancellationToken,
        Func<string, T?> read)
    {
        using var releaseRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.github.com/repos/ollama/ollama/releases/latest");
        releaseRequest.Headers.TryAddWithoutValidation("User-Agent", "SuperDev");
        releaseRequest.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var releaseResponse = await _http.SendAsync(releaseRequest, cancellationToken).ConfigureAwait(false);
        if (!releaseResponse.IsSuccessStatusCode)
            return default;

        var releaseJson = await releaseResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return read(releaseJson);
    }

    private static Uri? ChecksumUrl(string releaseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(releaseJson);
            if (!document.RootElement.TryGetProperty("assets", out var assets))
                return null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                if (!string.Equals(name, "sha256sum.txt", StringComparison.OrdinalIgnoreCase))
                    continue;
                var url = asset.TryGetProperty("browser_download_url", out var urlValue) ? urlValue.GetString() : null;
                return Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : null;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
