using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public sealed class ChecklistValidator
{
    public const int MaxItems = 20;
    public const int MaxTitleLength = 120;
    public const int MaxSummaryLength = 320;
    public const int MaxDescriptionLength = 220;
    public const int MaxAcceptanceLength = 180;

    private static readonly Regex ThinkBlock = new(
        @"<think\b[^>]*>.*?</think>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public ChecklistInspection ReadChecklist(string? content)
    {
        if (!TryDeserializeChecklist(content, out var checklist, out var repaired, out var failure))
            return Fail(failure is AiFailureKind.InvalidJson or AiFailureKind.EmptyResponse
                ? "checklist-json"
                : "checklist-validation", failure);

        return FinishChecklist(checklist, fromModel: true, repaired);
    }

    public ChecklistInspection ReadSection(string? content, string? preferredName = null)
    {
        if (!TryDeserializeSection(content, preferredName, out var section, out var repaired, out var failure))
            return Fail(failure is AiFailureKind.InvalidJson or AiFailureKind.EmptyResponse
                ? "checklist-json"
                : "checklist-validation", failure);

        var wrapped = FinishChecklist(new GeneratedChecklist
        {
            Title = "Section",
            Summary = section.Name,
            Sections = [section]
        }, fromModel: true, repaired);

        if (!wrapped.IsValid || wrapped.Checklist is null || wrapped.Checklist.Sections.Count == 0)
        {
            var kind = wrapped.Failure?.Kind is AiFailureKind.NoValidItems or AiFailureKind.NoSections
                ? wrapped.Failure.Kind
                : AiFailureKind.NoValidItems;
            return Fail("checklist-validation", kind);
        }

        return new ChecklistInspection
        {
            IsValid = true,
            Section = wrapped.Checklist.Sections[0],
            Notice = wrapped.Notice,
            NoticeIsAdjustment = wrapped.NoticeIsAdjustment
        };
    }

    public ChecklistInspection ReadCheck(string? content)
    {
        if (!TryDeserializeCheck(content, out var check, out var repaired, out var failure))
            return Fail(failure is AiFailureKind.InvalidJson or AiFailureKind.EmptyResponse
                ? "checklist-json"
                : "checklist-validation", failure);

        return FinishCheck(check, fromModel: true, repaired);
    }

    public ChecklistInspection Inspect(GeneratedChecklist checklist)
    {
        ArgumentNullException.ThrowIfNull(checklist);
        var copy = new GeneratedChecklist
        {
            Title = checklist.Title,
            Summary = checklist.Summary,
            Sections = checklist.Sections.ConvertAll(section => new GeneratedSection
            {
                Name = section.Name,
                Items = section.Items.ConvertAll(item => new GeneratedCheckItem
                {
                    Title = item.Title,
                    Description = item.Description,
                    Type = item.Type,
                    Priority = item.Priority,
                    AcceptanceCriteria = item.AcceptanceCriteria
                })
            })
        };
        return FinishChecklist(copy, fromModel: false, repaired: false);
    }

    private ChecklistInspection FinishChecklist(GeneratedChecklist checklist, bool fromModel, bool repaired)
    {
        var filled = 0;
        var adjusted = 0;
        checklist.Title = Limit(checklist.Title, MaxTitleLength, fromModel, ref adjusted);
        checklist.Summary = Limit(checklist.Summary, MaxSummaryLength, fromModel, ref adjusted);
        checklist.Sections ??= [];
        if (string.IsNullOrWhiteSpace(checklist.Title))
            return Fail("checklist-validation", AiFailureKind.MissingTitle);
        if (checklist.Sections.Count == 0)
            return Fail("checklist-validation", AiFailureKind.NoSections);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<GeneratedSection>();
        var count = 0;
        foreach (var section in checklist.Sections)
        {
            section.Name = Limit(section.Name, 60, fromModel, ref adjusted);
            if (string.IsNullOrWhiteSpace(section.Name))
            {
                section.Name = "General";
                adjusted++;
            }

            var items = new List<GeneratedCheckItem>();
            foreach (var item in section.Items)
            {
                var itemAdjusted = false;
                var itemFilled = false;
                var shortened = 0;
                item.Title = Limit(item.Title, MaxTitleLength, fromModel, ref shortened);
                if (string.IsNullOrWhiteSpace(item.Title))
                {
                    adjusted++;
                    continue;
                }

                if (!seen.Add(item.Title.Trim()))
                {
                    adjusted++;
                    continue;
                }

                item.Type = NormalizeType(item.Type, section.Name, ref itemAdjusted, ref itemFilled);
                item.Priority = NormalizePriority(item.Priority, ref itemAdjusted, ref itemFilled);
                item.Description = Limit(item.Description, MaxDescriptionLength, fromModel, ref shortened);
                item.AcceptanceCriteria = Limit(item.AcceptanceCriteria, MaxAcceptanceLength, fromModel, ref shortened);
                if (shortened > 0)
                    itemAdjusted = true;

                if (count >= MaxItems)
                {
                    adjusted++;
                    break;
                }

                if (itemAdjusted)
                    adjusted++;
                else if (itemFilled)
                    filled++;

                items.Add(item);
                count++;
            }

            if (items.Count == 0)
            {
                adjusted++;
                continue;
            }

            var existing = kept.Find(candidate =>
                string.Equals(candidate.Name, section.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                section.Items = items;
                kept.Add(section);
            }
            else
            {
                existing.Items.AddRange(items);
                adjusted++;
            }

            if (count >= MaxItems)
                break;
        }

        checklist.Sections = kept;
        if (kept.Count == 0)
            return Fail("checklist-validation", AiFailureKind.NoValidItems);

        if (repaired)
            adjusted = Math.Max(adjusted, 1);

        var notice = fromModel ? ChecklistVocabulary.Notice(filled, adjusted) : string.Empty;
        return new ChecklistInspection
        {
            IsValid = true,
            Checklist = checklist,
            Notice = notice,
            NoticeIsAdjustment = adjusted > 0
        };
    }

    private ChecklistInspection FinishCheck(GeneratedCheck check, bool fromModel, bool repaired)
    {
        var filled = 0;
        var adjusted = 0;
        check.Title = Limit(check.Title, MaxTitleLength, fromModel, ref adjusted);
        if (string.IsNullOrWhiteSpace(check.Title))
            return Fail("checklist-validation", AiFailureKind.NoValidItems);

        var categoryAdjusted = false;
        var categoryFilled = false;
        check.Category = NormalizeCategory(check.Category, ref categoryAdjusted, ref categoryFilled);
        if (categoryAdjusted)
            adjusted++;
        else if (categoryFilled)
            filled++;

        var priorityAdjusted = false;
        var priorityFilled = false;
        check.Priority = NormalizePriority(check.Priority, ref priorityAdjusted, ref priorityFilled);
        if (priorityAdjusted)
            adjusted++;
        else if (priorityFilled)
            filled++;

        var shortened = 0;
        check.Description = Limit(check.Description, MaxDescriptionLength, fromModel, ref shortened);
        check.AcceptanceCriteria = Limit(check.AcceptanceCriteria, MaxAcceptanceLength, fromModel, ref shortened);
        if (shortened > 0)
            adjusted++;

        check.Subtasks = NormalizeSubtasks(check.Subtasks, fromModel, ref adjusted);
        if (repaired)
            adjusted = Math.Max(adjusted, 1);

        var notice = fromModel ? ChecklistVocabulary.Notice(filled, adjusted) : string.Empty;
        return new ChecklistInspection
        {
            IsValid = true,
            Check = check,
            Notice = notice,
            NoticeIsAdjustment = adjusted > 0
        };
    }

    private static string NormalizeCategory(string? raw, ref bool adjusted, ref bool filled)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            filled = true;
            return CheckCategories.Default;
        }

        if (!TryNormalizeCategory(raw, out var category))
        {
            adjusted = true;
            return CheckCategories.Default;
        }

        if (!category.Equals(raw.Trim(), StringComparison.Ordinal))
            adjusted = true;
        return category;
    }

    private static List<string> NormalizeSubtasks(List<string>? subtasks, bool fromModel, ref int adjusted)
    {
        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subtask in subtasks ?? [])
        {
            var title = Limit(subtask, MaxTitleLength, fromModel, ref adjusted);
            if (string.IsNullOrWhiteSpace(title))
            {
                adjusted++;
                continue;
            }

            if (!seen.Add(title) || kept.Count >= 4)
            {
                adjusted++;
                continue;
            }

            kept.Add(title);
        }

        return kept;
    }

    private static string NormalizeType(string? raw, string? sectionName, ref bool adjusted, ref bool filled)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            filled = true;
            return DefaultTypeFor(sectionName);
        }

        if (!TryNormalizeType(raw, out var type))
        {
            adjusted = true;
            return ChecklistVocabulary.DefaultType;
        }

        if (!type.Equals(raw.Trim(), StringComparison.OrdinalIgnoreCase))
            adjusted = true;
        return type;
    }

    private static string DefaultTypeFor(string? sectionName)
    {
        var name = sectionName ?? string.Empty;
        if (ContainsAny(name, "investigat", "diagnos", "reproduc", "cause", "analys"))
            return "Investigation";
        if (ContainsAny(name, "verif", "test", "regress"))
            return "Verification";
        if (ContainsAny(name, "doc"))
            return "Documentation";
        return ChecklistVocabulary.DefaultType;
    }

    private static bool ContainsAny(string value, params string[] fragments)
    {
        foreach (var fragment in fragments)
        {
            if (value.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string NormalizePriority(string? raw, ref bool adjusted, ref bool filled)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            filled = true;
            return ChecklistVocabulary.DefaultPriority;
        }

        if (!TryNormalizePriority(raw, out var priority))
        {
            adjusted = true;
            return ChecklistVocabulary.DefaultPriority;
        }

        if (!priority.Equals(raw.Trim(), StringComparison.OrdinalIgnoreCase))
            adjusted = true;
        return priority;
    }

    private bool TryDeserializeChecklist(
        string? content,
        out GeneratedChecklist checklist,
        out bool repaired,
        out AiFailureKind failure)
    {
        checklist = new GeneratedChecklist();
        repaired = false;
        if (!TryPrepareJson(content, out var json, out repaired, out failure))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<GeneratedChecklist>(json, AiChecklistJson.SerializerOptions);
            if (parsed is null)
            {
                failure = AiFailureKind.InvalidJson;
                return false;
            }

            parsed.Sections ??= [];
            checklist = parsed;
            failure = AiFailureKind.InvalidJson;
            return true;
        }
        catch (JsonException ex)
        {
            AiDiagnostics.Fail("checklist-json", "Checklist JSON could not be deserialized"
                + (string.IsNullOrWhiteSpace(ex.Path) ? "." : " at " + ex.Path + "."));
            failure = AiFailureKind.InvalidJson;
            return false;
        }
    }

    private bool TryDeserializeSection(
        string? content,
        string? preferredName,
        out GeneratedSection section,
        out bool repaired,
        out AiFailureKind failure)
    {
        section = new GeneratedSection();
        repaired = false;
        if (!TryPrepareJson(content, out var json, out repaired, out failure))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                var inner = root.GetString();
                return TryDeserializeSection(inner, preferredName, out section, out repaired, out failure);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                failure = AiFailureKind.InvalidJson;
                return false;
            }

            if (HasProperty(root, "sections"))
            {
                var checklist = JsonSerializer.Deserialize<GeneratedChecklist>(json, AiChecklistJson.SerializerOptions);
                var chosen = ChooseSection(checklist?.Sections, preferredName);
                if (chosen is null)
                {
                    failure = AiFailureKind.NoValidItems;
                    return false;
                }

                section = chosen;
                failure = AiFailureKind.InvalidJson;
                return true;
            }

            var parsed = JsonSerializer.Deserialize<GeneratedSection>(json, AiChecklistJson.SerializerOptions);
            if (parsed is null)
            {
                failure = AiFailureKind.InvalidJson;
                return false;
            }

            section = parsed;
            failure = AiFailureKind.InvalidJson;
            return true;
        }
        catch (JsonException ex)
        {
            AiDiagnostics.Fail("checklist-json", "Section JSON could not be deserialized"
                + (string.IsNullOrWhiteSpace(ex.Path) ? "." : " at " + ex.Path + "."));
            failure = AiFailureKind.InvalidJson;
            return false;
        }
    }

    private bool TryDeserializeCheck(
        string? content,
        out GeneratedCheck check,
        out bool repaired,
        out AiFailureKind failure)
    {
        check = new GeneratedCheck();
        repaired = false;
        if (!TryPrepareJson(content, out var json, out repaired, out failure))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
                return TryDeserializeCheck(root.GetString(), out check, out repaired, out failure);

            if (root.ValueKind != JsonValueKind.Object)
            {
                failure = AiFailureKind.InvalidJson;
                return false;
            }

            if (HasProperty(root, "sections"))
            {
                var checklist = JsonSerializer.Deserialize<GeneratedChecklist>(json, AiChecklistJson.SerializerOptions);
                if (!TryFirstCheck(checklist?.Sections, out check))
                {
                    failure = AiFailureKind.NoValidItems;
                    return false;
                }

                failure = AiFailureKind.InvalidJson;
                return true;
            }

            if (HasProperty(root, "items"))
            {
                var section = JsonSerializer.Deserialize<GeneratedSection>(json, AiChecklistJson.SerializerOptions);
                if (!TryFirstCheck(section is null ? null : [section], out check))
                {
                    failure = AiFailureKind.NoValidItems;
                    return false;
                }

                failure = AiFailureKind.InvalidJson;
                return true;
            }

            var parsed = JsonSerializer.Deserialize<GeneratedCheck>(json, AiChecklistJson.SerializerOptions);
            if (parsed is null)
            {
                failure = AiFailureKind.InvalidJson;
                return false;
            }

            check = parsed;
            failure = AiFailureKind.InvalidJson;
            return true;
        }
        catch (JsonException ex)
        {
            AiDiagnostics.Fail("checklist-json", "Check JSON could not be deserialized"
                + (string.IsNullOrWhiteSpace(ex.Path) ? "." : " at " + ex.Path + "."));
            failure = AiFailureKind.InvalidJson;
            return false;
        }
    }

    private static bool TryFirstCheck(List<GeneratedSection>? sections, out GeneratedCheck check)
    {
        check = new GeneratedCheck();
        if (sections is null)
            return false;

        foreach (var section in sections)
        {
            foreach (var item in section.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Title))
                    continue;

                check = new GeneratedCheck
                {
                    Title = item.Title,
                    Category = section.Name,
                    Priority = item.Priority,
                    Description = item.Description,
                    AcceptanceCriteria = item.AcceptanceCriteria
                };
                return true;
            }
        }

        return false;
    }

    private static GeneratedSection? ChooseSection(List<GeneratedSection>? sections, string? preferredName)
    {
        if (sections is null || sections.Count == 0)
            return null;

        GeneratedSection? fallback = null;
        foreach (var candidate in sections)
        {
            if (candidate.Items.Count == 0)
                continue;

            fallback ??= candidate;
            if (string.IsNullOrWhiteSpace(preferredName)
                || candidate.Name.Equals(preferredName.Trim(), StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return fallback;
    }

    private static bool HasProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static ChecklistInspection Fail(string stage, AiFailureKind kind, IReadOnlyList<string>? warnings = null)
    {
        var message = AiMessages.For(kind, AIModelManager.DefaultModel);
        AiDiagnostics.Fail(stage, message);
        return new ChecklistInspection
        {
            IsValid = false,
            Failure = new AiFailure
            {
                Kind = kind,
                Message = message
            },
            Warnings = warnings ?? []
        };
    }

    private bool TryPrepareJson(string? content, out string json, out bool repaired, out AiFailureKind failure)
    {
        json = string.Empty;
        repaired = false;
        var extracted = ExtractJson(content);
        if (extracted is null)
        {
            failure = string.IsNullOrWhiteSpace(content) ? AiFailureKind.EmptyResponse : AiFailureKind.InvalidJson;
            return false;
        }

        if (IsJsonObject(extracted))
        {
            json = extracted;
            failure = AiFailureKind.InvalidJson;
            return true;
        }

        var restored = RepairTruncatedJson(extracted);
        if (restored is null || !IsJsonObject(restored))
        {
            failure = AiFailureKind.InvalidJson;
            return false;
        }

        json = restored;
        repaired = true;
        failure = AiFailureKind.InvalidJson;
        return true;
    }

    private static bool IsJsonObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? RepairTruncatedJson(string json)
    {
        if (TryCloseTruncation(json, out var closed) && IsJsonObject(closed))
            return closed;

        var cuts = CompleteValueCuts(json);
        for (var index = cuts.Count - 1; index >= 0 && index >= cuts.Count - 16; index--)
        {
            var candidate = json[..cuts[index].End] + cuts[index].Closers;
            if (IsJsonObject(candidate))
                return candidate;
        }

        return null;
    }

    private static bool TryCloseTruncation(string json, out string closed)
    {
        closed = json;
        var stack = new List<char>();
        var inString = false;
        var escape = false;
        foreach (var character in json)
        {
            if (inString)
            {
                if (escape)
                    escape = false;
                else if (character == '\\')
                    escape = true;
                else if (character == '"')
                    inString = false;
                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    stack.Add('}');
                    break;
                case '[':
                    stack.Add(']');
                    break;
                case '}':
                case ']':
                    if (stack.Count == 0 || stack[^1] != character)
                        return false;
                    stack.RemoveAt(stack.Count - 1);
                    break;
            }
        }

        if (stack.Count == 0 && !inString)
            return false;
        if (escape)
            return false;

        var trimmed = json.TrimEnd();
        if (!inString && trimmed.Length > 0 && trimmed[^1] is ',' or ':')
            return false;

        if (inString)
            closed += "\"";
        closed += Closers(stack);
        return true;
    }

    private readonly record struct JsonCut(int End, string Closers);

    private static List<JsonCut> CompleteValueCuts(string json)
    {
        var cuts = new List<JsonCut>();
        var stack = new List<char>();
        var inString = false;
        var escape = false;
        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (inString)
            {
                if (escape)
                    escape = false;
                else if (character == '\\')
                    escape = true;
                else if (character == '"')
                    inString = false;
                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    stack.Add('}');
                    break;
                case '[':
                    stack.Add(']');
                    break;
                case '}':
                case ']':
                    if (stack.Count == 0 || stack[^1] != character)
                        return cuts;
                    stack.RemoveAt(stack.Count - 1);
                    if (stack.Count > 0)
                        cuts.Add(new JsonCut(index + 1, Closers(stack)));
                    break;
            }
        }

        return cuts;
    }

    private static string Closers(List<char> stack)
    {
        var closers = new char[stack.Count];
        for (var index = 0; index < stack.Count; index++)
            closers[index] = stack[stack.Count - 1 - index];
        return new string(closers);
    }

    public static string? ExtractJson(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var stripped = ThinkBlock.Replace(content, string.Empty).Trim();
        return ExtractObject(stripped) ?? ExtractObject(content.Trim());
    }

    private static string? ExtractObject(string text)
    {
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = text.IndexOf('\n');
            if (newline >= 0)
                text = text[(newline + 1)..];
            var fence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
                text = text[..fence];
            text = text.Trim();
        }

        var start = text.IndexOf('{');
        if (start < 0)
            return null;

        var tail = text[start..].Trim();
        var end = tail.LastIndexOf('}');
        if (end > 0)
        {
            var closed = StripTrailingCommas(tail[..(end + 1)]);
            if (IsJsonObject(closed))
                return closed;
        }

        return StripTrailingCommas(tail);
    }

    private static string StripTrailingCommas(string json)
    {
        var buffer = new StringBuilder(json.Length);
        var inString = false;
        var escape = false;
        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (inString)
            {
                buffer.Append(character);
                if (escape)
                    escape = false;
                else if (character == '\\')
                    escape = true;
                else if (character == '"')
                    inString = false;
                continue;
            }

            if (character == '"')
            {
                inString = true;
                buffer.Append(character);
                continue;
            }

            if (character == ',')
            {
                var look = index + 1;
                while (look < json.Length && char.IsWhiteSpace(json[look]))
                    look++;
                if (look < json.Length && json[look] is '}' or ']')
                    continue;
            }

            buffer.Append(character);
        }

        return buffer.ToString();
    }

    public static bool TryNormalizeType(string? raw, out string type)
    {
        var value = raw?.Trim() ?? string.Empty;
        if (IsAny(value, "Investigation", "Investigate", "Analysis", "Diagnosis", "Debugging", "Reproduce"))
        {
            type = "Investigation";
            return true;
        }

        if (IsAny(value, "Verification", "Verify", "Testing", "Test", "QA"))
        {
            type = "Verification";
            return true;
        }

        if (IsAny(value, "Documentation", "Document", "Docs"))
        {
            type = "Documentation";
            return true;
        }

        if (IsAny(value, "Implementation", "Implement", "Development", "Fix", "Feature", "Task", "Action", "Step", "Check"))
        {
            type = "Implementation";
            return true;
        }

        type = string.Empty;
        return false;
    }

    public static bool TryNormalizeCategory(string? raw, out string category)
    {
        var value = raw?.Trim() ?? string.Empty;
        foreach (var known in CheckCategories.All)
        {
            if (value.Equals(known, StringComparison.OrdinalIgnoreCase))
            {
                category = known;
                return true;
            }
        }

        if (ContainsAny(value, "understand", "investigat", "diagnos", "reproduc", "analys"))
        {
            category = "Understand";
            return true;
        }

        if (ContainsAny(value, "communicat"))
        {
            category = "Communication";
            return true;
        }

        if (ContainsAny(value, "deploy", "release"))
        {
            category = "Deployment";
            return true;
        }

        if (ContainsAny(value, "merge"))
        {
            category = "Merge";
            return true;
        }

        if (ContainsAny(value, "closure", "close-out", "wrap-up", "wrap up"))
        {
            category = "Closure";
            return true;
        }

        if (ContainsAny(value, "test", "verif", "qa"))
        {
            category = "Testing";
            return true;
        }

        if (IsAny(value, "PR", "Review", "Code Review", "Pull Request"))
        {
            category = "PR";
            return true;
        }

        if (ContainsAny(value, "develop", "implement", "fix", "feature"))
        {
            category = "Development";
            return true;
        }

        category = string.Empty;
        return false;
    }

    public static bool TryNormalizePriority(string? raw, out string priority)
    {
        var value = raw?.Trim() ?? string.Empty;
        if (IsAny(value, "Critical", "High"))
        {
            priority = "Critical";
            return true;
        }

        if (IsAny(value, "Conditional", "Low", "Optional", "Nice to have", "Nice-to-have"))
        {
            priority = "Conditional";
            return true;
        }

        if (IsAny(value, "Review"))
        {
            priority = "Review";
            return true;
        }

        if (IsAny(value, "Required", "Medium", "Normal", "Mandatory", "Must"))
        {
            priority = "Required";
            return true;
        }

        priority = string.Empty;
        return false;
    }

    private static bool IsAny(string value, params string[] names)
    {
        foreach (var name in names)
        {
            if (value.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string Limit(string? value, int max, bool fromModel, ref int adjusted)
    {
        var text = value?.Trim() ?? string.Empty;
        if (!fromModel || text.Length <= max)
            return text;

        adjusted++;
        return text[..max].TrimEnd();
    }
}
