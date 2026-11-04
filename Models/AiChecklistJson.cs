using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperDev.Models;

public static class AiChecklistJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public static string FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                var text = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
        }

        return string.Empty;
    }
}

/// <summary>
/// Reads a section whether the model named it <c>section_title</c> or <c>name</c>.
/// Unknown properties are ignored.
/// </summary>
public sealed class GeneratedSectionConverter : JsonConverter<GeneratedSection>
{
    public override GeneratedSection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return new GeneratedSection();
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        var section = new GeneratedSection
        {
            Name = AiChecklistJson.FirstString(element, "section_title", "sectionTitle", "name", "title")
        };

        if (!TryGetItems(element, out var items))
            return section;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            section.Items.Add(new GeneratedCheckItem
            {
                Title = AiChecklistJson.FirstString(item, "title", "name"),
                Description = AiChecklistJson.FirstString(item, "description", "detail"),
                Type = AiChecklistJson.FirstString(item, "type", "itemType", "item_type"),
                Priority = AiChecklistJson.FirstString(item, "priority"),
                AcceptanceCriteria = AiChecklistJson.FirstString(item, "acceptanceCriteria", "acceptance_criteria", "acceptance")
            });
        }

        return section;
    }

    public override void Write(Utf8JsonWriter writer, GeneratedSection value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("section_title", value.Name);
        writer.WritePropertyName("items");
        JsonSerializer.Serialize(writer, value.Items, options);
        writer.WriteEndObject();
    }

    private static bool TryGetItems(JsonElement element, out JsonElement items)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals("items", StringComparison.OrdinalIgnoreCase))
                continue;
            if (property.Value.ValueKind != JsonValueKind.Array)
                break;
            items = property.Value;
            return true;
        }

        items = default;
        return false;
    }
}

public sealed class GeneratedCheckConverter : JsonConverter<GeneratedCheck>
{
    public override GeneratedCheck Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return new GeneratedCheck();
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        return new GeneratedCheck
        {
            Title = AiChecklistJson.FirstString(element, "title", "name"),
            Category = AiChecklistJson.FirstString(element, "category", "section_title", "sectionTitle", "section"),
            Priority = AiChecklistJson.FirstString(element, "priority"),
            Description = AiChecklistJson.FirstString(element, "description", "detail", "notes"),
            AcceptanceCriteria = AiChecklistJson.FirstString(element, "acceptanceCriteria", "acceptance_criteria", "acceptance"),
            Subtasks = ReadSubtasks(element)
        };
    }

    public override void Write(Utf8JsonWriter writer, GeneratedCheck value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("title", value.Title);
        writer.WriteString("category", value.Category);
        writer.WriteString("priority", value.Priority);
        writer.WriteString("description", value.Description);
        writer.WriteString("acceptanceCriteria", value.AcceptanceCriteria);
        writer.WritePropertyName("subtasks");
        writer.WriteStartArray();
        foreach (var subtask in value.Subtasks)
            writer.WriteStringValue(subtask);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static List<string> ReadSubtasks(JsonElement element)
    {
        var subtasks = new List<string>();
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals("subtasks", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("steps", StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var single = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(single))
                    subtasks.Add(single.Trim());
                break;
            }

            if (property.Value.ValueKind != JsonValueKind.Array)
                break;

            foreach (var item in property.Value.EnumerateArray())
            {
                var title = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Object => AiChecklistJson.FirstString(item, "title", "name"),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(title))
                    subtasks.Add(title.Trim());
            }

            break;
        }

        return subtasks;
    }
}
