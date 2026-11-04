using System;
using System.Diagnostics;
using SuperDev.Models;

namespace SuperDev.Services;

public static class LocalFile
{
    public static bool TryOpen(string? path)
    {
        if (!EvidenceKind.FileExists(path))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo(path!)
            {
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
