using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperDev.Services.Ai;

public sealed class OllamaChatEnvelope
{
    [JsonPropertyName("message")]
    public OllamaChatMessage? Message { get; set; }
}

public sealed class OllamaChatMessage
{
    [JsonPropertyName("content")]
    [JsonConverter(typeof(JsonTextOrObjectConverter))]
    public string? Content { get; set; }

    [JsonPropertyName("thinking")]
    [JsonConverter(typeof(JsonTextOrObjectConverter))]
    public string? Thinking { get; set; }
}

/// <summary>
/// Ollama usually returns message text as a string. Some responses put the JSON object directly in that field.
/// </summary>
public sealed class JsonTextOrObjectConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                using (var document = JsonDocument.ParseValue(ref reader))
                    return document.RootElement.GetRawText();
            case JsonTokenType.Null:
                return null;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
