using System;
using System.Diagnostics;

namespace SuperDev.Services;

public static class ExternalLink
{
    public static bool CanOpen(string? value) =>
        TryCreateHttpUri(value, out _);

    public static bool TryOpen(string? value)
    {
        if (!TryCreateHttpUri(value, out var uri))
            return false;

        try
        {
            Open(BrowserUrl(uri));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void Open(string url)
    {
        if (OperatingSystem.IsWindows())
        {
            // cmd expands % and treats an unquoted & as a second command.
            // The empty title keeps start from swallowing the address.
            var quoted = url.Replace("%", "%%", StringComparison.Ordinal)
                .Replace("\"", "", StringComparison.Ordinal);
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c start \"\" \"" + quoted + "\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            return;
        }

        Process.Start(new ProcessStartInfo(url)
        {
            UseShellExecute = true
        });
    }

    private static string BrowserUrl(Uri uri)
    {
        // ASP.NET rejects a raw colon in the path. Leave the host and port alone.
        var authority = uri.GetLeftPart(UriPartial.Authority);
        var rest = uri.AbsoluteUri.Length > authority.Length
            ? uri.AbsoluteUri[authority.Length..]
            : string.Empty;
        var cut = rest.IndexOfAny(['?', '#']);
        var path = cut >= 0 ? rest[..cut] : rest;
        var tail = cut >= 0 ? rest[cut..] : string.Empty;
        if (path.Contains(':'))
            path = path.Replace(":", "%3A", StringComparison.Ordinal);
        return authority + path + tail;
    }

    private static bool TryCreateHttpUri(string? value, out Uri uri)
    {
        uri = null!;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return false;

        uri = parsed;
        return true;
    }
}
