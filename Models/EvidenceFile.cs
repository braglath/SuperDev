using System;
using System.IO;

namespace SuperDev.Models;

public sealed class EvidenceFile
{
    public string Path { get; set; } = string.Empty;
}

public static class EvidenceKind
{
    public static readonly string[] ImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".heic", ".tif", ".tiff"
    ];

    public static readonly string[] VideoExtensions =
    [
        ".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi"
    ];

    public static bool IsImage(string? path) =>
        HasExtension(path, ImageExtensions);

    public static bool IsVideo(string? path) =>
        HasExtension(path, VideoExtensions);

    public static bool IsSupported(string? path) =>
        IsImage(path) || IsVideo(path);

    public static string[] ImagePatterns() =>
        Patterns(ImageExtensions);

    public static string[] VideoPatterns() =>
        Patterns(VideoExtensions);

    public static string[] AllPatterns() =>
        Patterns([.. ImageExtensions, .. VideoExtensions]);

    public static string DisplayName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Missing file";

        try
        {
            var name = Path.GetFileName(path.Trim());
            return string.IsNullOrWhiteSpace(name) ? path.Trim() : name;
        }
        catch (ArgumentException)
        {
            return path.Trim();
        }
    }

    public static bool FileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return false;
        }
    }

    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static string[] Patterns(string[] extensions)
    {
        var patterns = new string[extensions.Length];
        for (var index = 0; index < extensions.Length; index++)
            patterns[index] = "*" + extensions[index];

        return patterns;
    }

    private static bool HasExtension(string? path, string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string extension;
        try
        {
            extension = Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            return false;
        }

        foreach (var candidate in extensions)
        {
            if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
