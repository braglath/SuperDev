using System;
using System.Collections.Generic;
using System.Text.Json;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public static class OllamaPullProgress
{
    public static bool TryApply(
        string? line,
        IDictionary<string, (long Completed, long Total)> layers,
        out ModelDownloadProgress progress)
    {
        progress = default;
        if (string.IsNullOrWhiteSpace(line) || layers is null)
            return false;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var status = ReadString(root, "status") ?? string.Empty;
            var failed = status.Equals("error", StringComparison.OrdinalIgnoreCase)
                || root.TryGetProperty("error", out _);
            var succeeded = status.Equals("success", StringComparison.OrdinalIgnoreCase);
            var digest = ReadString(root, "digest");
            var completed = ReadInt(root, "completed");
            var total = ReadInt(root, "total");
            if (!string.IsNullOrWhiteSpace(digest) && (completed is not null || total is not null))
            {
                var current = layers.TryGetValue(digest, out var existing) ? existing : default;
                layers[digest] = (
                    completed ?? current.Completed,
                    total ?? current.Total);
            }

            long done = 0;
            long sum = 0;
            var totalsKnown = layers.Count > 0;
            foreach (var layer in layers.Values)
            {
                done += layer.Completed;
                if (layer.Total <= 0)
                    totalsKnown = false;
                else
                    sum += layer.Total;
            }

            if (layers.Count == 0 && completed is not null)
            {
                done = completed.Value;
                if (total is > 0)
                {
                    sum = total.Value;
                    totalsKnown = true;
                }
            }

            progress = new ModelDownloadProgress(done, totalsKnown && sum > 0 ? sum : null, status, succeeded, failed);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static long? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        return null;
    }
}
