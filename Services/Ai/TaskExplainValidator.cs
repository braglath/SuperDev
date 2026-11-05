using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public sealed class TaskExplainValidator
{
    public const int MaxSteps = 6;

    public const string TruncatedWarning = "Only the first 6 steps are shown.";

    private static readonly JsonDocument SchemaDocument = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "whyItMatters": { "type": "string" },
            "steps": {
              "type": "array",
              "maxItems": 6,
              "items": { "type": "string" }
            },
            "doneWhen": { "type": "string" }
          },
          "required": ["summary", "whyItMatters", "steps", "doneWhen"]
        }
        """);

    public static JsonElement Schema => SchemaDocument.RootElement;

    public TaskExplainInspection Read(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Fail(AiFailureKind.EmptyResponse);

        var json = ChecklistValidator.ExtractJson(content);
        if (string.IsNullOrWhiteSpace(json))
            return Fail(AiFailureKind.InvalidJson);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException)
        {
            return Fail(AiFailureKind.InvalidJson);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail(AiFailureKind.InvalidChecklist);

            var summary = Sentence(Text(root, "summary"), 420);
            if (summary.Length == 0)
                return Fail(AiFailureKind.InvalidChecklist);

            var warnings = new List<string>();
            return new TaskExplainInspection
            {
                IsValid = true,
                Explanation = new TaskExplanation
                {
                    Summary = summary,
                    WhyItMatters = Sentence(Text(root, "whyItMatters", "why_it_matters"), 320),
                    Steps = ReadSteps(root, warnings),
                    DoneWhen = Sentence(Text(root, "doneWhen", "done_when"), 240)
                },
                Warnings = warnings
            };
        }
    }

    private static IReadOnlyList<string> ReadSteps(JsonElement root, List<string> warnings)
    {
        if (!TryProperty(root, "steps", out var value) && !TryProperty(root, "howTo", out value))
            return [];

        var raw = new List<string>();
        if (value.ValueKind == JsonValueKind.String)
        {
            raw.AddRange(SplitSteps(value.GetString()));
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    continue;

                var step = Sentence(entry.GetString(), 240);
                if (step.Length > 0)
                    raw.Add(step);
            }
        }

        if (raw.Count <= MaxSteps)
            return raw;

        warnings.Add(TruncatedWarning);
        return raw.GetRange(0, MaxSteps);
    }

    private static List<string> SplitSteps(string? value)
    {
        var steps = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
            return steps;

        foreach (var line in value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var step = Sentence(line, 240);
            if (step.Length > 0)
                steps.Add(step);
        }

        return steps;
    }

    private static string? Text(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    private static string Sentence(string? value, int max)
    {
        var text = StripMarker(value);
        if (text.Length == 0 || max <= 0)
            return string.Empty;

        var compact = new StringBuilder(Math.Min(text.Length, max));
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = compact.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                if (compact.Length + 1 >= max)
                    break;
                compact.Append(' ');
                pendingSpace = false;
            }

            if (compact.Length + 1 > max)
                break;

            compact.Append(character);
        }

        return compact.ToString();
    }

    private static string StripMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = value.Trim();
        if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("• ", StringComparison.Ordinal))
            return text[2..].Trim();

        var index = 0;
        while (index < text.Length && char.IsDigit(text[index]))
            index++;

        if (index > 0 && index < text.Length && text[index] is '.' or ')' or ':')
            return text[(index + 1)..].Trim();

        return text;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static TaskExplainInspection Fail(AiFailureKind kind) => new()
    {
        IsValid = false,
        FailureKind = kind
    };
}
