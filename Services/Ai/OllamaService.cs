using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public sealed class OllamaRequestException : Exception
{
    public OllamaRequestException(AiFailureKind kind, string stage = "ollama-request")
        : base(AiMessages.For(kind, AIModelManager.DefaultModel))
    {
        Kind = kind;
        Stage = stage;
    }

    public AiFailureKind Kind { get; }

    public string Stage { get; }
}

public sealed class OllamaService : ILocalAIService, IDisposable
{
    public const string DefaultModel = AIModelManager.DefaultModel;

    public static readonly Uri LocalEndpoint = new("http://127.0.0.1:11434/");

    private const int MaxResponseCharacters = 512_000;

    private static readonly JsonDocument ChecklistSchemaDocument = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "summary": { "type": "string" },
            "sections": {
              "type": "array",
              "maxItems": 4,
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "section_title": { "type": "string" },
                  "items": {
                    "type": "array",
                    "maxItems": 4,
                    "items": {
                      "type": "object",
                      "properties": {
                        "title": { "type": "string" },
                        "description": { "type": "string" },
                        "type": { "type": "string", "enum": ["Investigation", "Implementation", "Verification", "Documentation"] },
                        "priority": { "type": "string", "enum": ["Critical", "Required", "Conditional", "Review"] },
                        "acceptanceCriteria": { "type": "string" }
                      },
                      "required": ["title"]
                    }
                  }
                },
                "required": ["items"]
              }
            }
          },
          "required": ["title", "sections"]
        }
        """);

    private static readonly JsonDocument SectionSchemaDocument = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" },
            "section_title": { "type": "string" },
            "items": {
              "type": "array",
              "maxItems": 5,
              "items": {
                "type": "object",
                "properties": {
                        "title": { "type": "string" },
                        "description": { "type": "string" },
                        "type": { "type": "string", "enum": ["Investigation", "Implementation", "Verification", "Documentation"] },
                        "priority": { "type": "string", "enum": ["Critical", "Required", "Conditional", "Review"] },
                        "acceptanceCriteria": { "type": "string" }
                },
                "required": ["title"]
              }
            }
          },
          "required": ["items"]
        }
        """);

    private static readonly JsonDocument CheckSchemaDocument = JsonDocument.Parse(BuildCheckSchema());

    private static JsonElement ChecklistSchema => ChecklistSchemaDocument.RootElement;

    private static JsonElement SectionSchema => SectionSchemaDocument.RootElement;

    private static JsonElement CheckSchema => CheckSchemaDocument.RootElement;

    private static string BuildCheckSchema()
    {
        var categories = string.Join(", ", CheckCategories.All.Select(name => "\"" + name + "\""));
        return $$"""
            {
              "type": "object",
              "properties": {
                "title": { "type": "string" },
                "category": { "type": "string", "enum": [{{categories}}] },
                "priority": { "type": "string", "enum": ["Critical", "Required", "Conditional", "Review"] },
                "description": { "type": "string" },
                "acceptanceCriteria": { "type": "string" },
                "subtasks": {
                  "type": "array",
                  "maxItems": 4,
                  "items": { "type": "string" }
                }
              },
              "required": ["title"]
            }
            """;
    }

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly PromptBuilder _prompts;
    private readonly ChecklistValidator _validator;
    private readonly string _model;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _generateTimeout;

    public OllamaService(HttpClient httpClient)
        : this(httpClient, ownsClient: false, prompts: null, validator: null, modelName: null, probeTimeout: null, generateTimeout: null)
    {
    }

    public OllamaService(
        HttpClient httpClient,
        PromptBuilder? prompts,
        ChecklistValidator? validator,
        string? modelName,
        TimeSpan? probeTimeout,
        TimeSpan? generateTimeout)
        : this(httpClient, ownsClient: false, prompts, validator, modelName, probeTimeout, generateTimeout)
    {
    }

    private OllamaService(
        HttpClient httpClient,
        bool ownsClient,
        PromptBuilder? prompts,
        ChecklistValidator? validator,
        string? modelName,
        TimeSpan? probeTimeout,
        TimeSpan? generateTimeout)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsClient = ownsClient;
        _prompts = prompts ?? new PromptBuilder();
        _validator = validator ?? new ChecklistValidator();
        _model = string.IsNullOrWhiteSpace(modelName) ? DefaultModel : modelName.Trim();
        _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(4);
        _generateTimeout = generateTimeout ?? TimeSpan.FromSeconds(120);
    }

    public static OllamaService CreateLocal()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = LocalEndpoint,
            Timeout = Timeout.InfiniteTimeSpan
        };
        return new OllamaService(client, ownsClient: true, prompts: null, validator: null, modelName: null, probeTimeout: null, generateTimeout: null);
    }

    public string ModelName => _model;

    public async Task<OllamaAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "api/tags", content: null, _probeTimeout, cancellationToken)
                .ConfigureAwait(false);
            return new OllamaAvailability(response.IsSuccessStatusCode
                ? OllamaReachability.Reachable
                : OllamaReachability.Unavailable);
        }
        catch (OllamaRequestException ex) when (ex.Kind == AiFailureKind.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OllamaRequestException ex)
        {
            return new OllamaAvailability(ex.Kind switch
            {
                AiFailureKind.ConnectionRefused => OllamaReachability.ConnectionRefused,
                AiFailureKind.Timeout => OllamaReachability.TimedOut,
                _ => OllamaReachability.Unavailable
            });
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/tags", content: null, _probeTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new OllamaRequestException(AiFailureKind.Unexpected);

        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseModelNames(body);
    }

    public async Task<bool> IsModelAvailableAsync(string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return false;

        var models = await ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return models.Any(installed => ModelsMatch(installed, modelName));
    }

    public async Task PullModelAsync(
        string modelName,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        EnsureLoopback();
        try
        {
        var payload = new JsonObject
        {
            ["model"] = JsonValue.Create(string.IsNullOrWhiteSpace(modelName) ? _model : modelName.Trim()),
            ["stream"] = JsonValue.Create(true)
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/pull")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new OllamaRequestException(AiFailureKind.Unexpected, "model-download");

        await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var layers = new Dictionary<string, (long Completed, long Total)>(StringComparer.Ordinal);
        while (true)
        {
            var line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
            if (line is null)
                break;
            if (!OllamaPullProgress.TryApply(line, layers, out var update))
                continue;

            progress?.Report(update);
            if (update.Failed)
                throw new OllamaRequestException(AiFailureKind.Unexpected, "model-download");
            if (update.Succeeded)
                return;
        }

        throw new OllamaRequestException(AiFailureKind.Unexpected, "model-download");
        }
        catch (OllamaRequestException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new OllamaRequestException(AiFailureKind.Timeout, "model-download");
        }
        catch (HttpRequestException)
        {
            throw new OllamaRequestException(AiFailureKind.NotRunning, "model-download");
        }
    }

    public async Task<bool> DescribeModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(
                HttpMethod.Post,
                "api/show",
                ModelJson(modelName),
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OllamaRequestException)
        {
            return false;
        }
    }

    public async Task<bool> LoadModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["model"] = JsonValue.Create(string.IsNullOrWhiteSpace(modelName) ? _model : modelName.Trim()),
            ["prompt"] = JsonValue.Create("ok"),
            ["stream"] = JsonValue.Create(false),
            ["keep_alive"] = JsonValue.Create("1m"),
            ["options"] = new JsonObject
            {
                ["num_predict"] = JsonValue.Create(1)
            }
        };
        try
        {
            using var response = await SendAsync(
                HttpMethod.Post,
                "api/generate",
                new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                TimeSpan.FromSeconds(120),
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OllamaRequestException)
        {
            return false;
        }
    }

    private StringContent ModelJson(string modelName)
    {
        var payload = new JsonObject
        {
            ["model"] = JsonValue.Create(string.IsNullOrWhiteSpace(modelName) ? _model : modelName.Trim())
        };
        return new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
    }

    private void EnsureLoopback()
    {
        if (_http.BaseAddress is not { IsLoopback: true })
            throw new OllamaRequestException(AiFailureKind.Unexpected);
    }

    public Task<AiGenerationResult> GenerateChecklistAsync(
        AiChecklistRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prompt = _prompts.BuildChecklist(request);
        return GenerateAsync(
            prompt,
            ChecklistSchema,
            numPredict: 4096,
            cancellationToken,
            content => AiGenerationResult.FromChecklist(_validator.ReadChecklist(content), _model));
    }

    public Task<AiGenerationResult> GenerateSectionAsync(
        AiChecklistRequest request,
        string sectionName,
        IReadOnlyList<string> existingTitles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prompt = _prompts.BuildSection(request, sectionName, existingTitles ?? []);
        return GenerateAsync(
            prompt,
            SectionSchema,
            numPredict: 2048,
            cancellationToken,
            content => AiGenerationResult.FromSection(_validator.ReadSection(content, sectionName), _model));
    }

    public Task<AiGenerationResult> GenerateCheckAsync(
        CheckGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prompt = _prompts.BuildCheck(request);
        return GenerateAsync(
            prompt,
            CheckSchema,
            numPredict: 1024,
            cancellationToken,
            content => AiGenerationResult.FromCheck(_validator.ReadCheck(content), _model));
    }

    public async Task<string> RequestStructuredAsync(
        Prompt prompt,
        JsonElement schema,
        int numPredict,
        CancellationToken cancellationToken = default)
    {
        if (!await IsModelAvailableAsync(_model, cancellationToken).ConfigureAwait(false))
            throw new OllamaRequestException(AiFailureKind.ModelMissing, "model-check");

        var content = await ChatAsync(prompt, schema, numPredict, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
            throw new OllamaRequestException(AiFailureKind.EmptyResponse, "message-content");

        return content;
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }

    public static bool ModelsMatch(string installed, string requested)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(requested))
            return false;

        return installed.Trim().Equals(requested.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AiGenerationResult> GenerateAsync(
        Prompt prompt,
        JsonElement schema,
        int numPredict,
        CancellationToken cancellationToken,
        Func<string, AiGenerationResult> parse)
    {
        try
        {
            if (!await IsModelAvailableAsync(_model, cancellationToken).ConfigureAwait(false))
            {
                var missing = AiGenerationResult.Fail(AiFailureKind.ModelMissing, _model);
                AiDiagnostics.Fail("model-check", missing.Failure!.Message);
                return missing;
            }

            var content = await ChatAsync(prompt, schema, numPredict, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
            {
                var empty = AiGenerationResult.Fail(AiFailureKind.EmptyResponse, _model);
                AiDiagnostics.Fail("message-content", empty.Failure!.Message);
                return empty;
            }

            return parse(content);
        }
        catch (OllamaRequestException ex)
        {
            AiDiagnostics.Fail(ex.Stage, ex.Message);
            return AiGenerationResult.Fail(ex.Kind, _model);
        }
        catch (OperationCanceledException)
        {
            return AiGenerationResult.Fail(AiFailureKind.Cancelled, _model);
        }
    }

    private async Task<string> ChatAsync(
        Prompt prompt,
        JsonElement schema,
        int numPredict,
        CancellationToken cancellationToken)
    {
        using (var first = await PostChatAsync(prompt, schema, useSchema: true, includeThink: true, numPredict, cancellationToken)
            .ConfigureAwait(false))
        {
            if (first.StatusCode != HttpStatusCode.BadRequest)
                return await ReadChatContentAsync(first, cancellationToken).ConfigureAwait(false);
        }

        using var second = await PostChatAsync(prompt, schema, useSchema: false, includeThink: false, numPredict, cancellationToken)
            .ConfigureAwait(false);
        return await ReadChatContentAsync(second, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PostChatAsync(
        Prompt prompt,
        JsonElement schema,
        bool useSchema,
        bool includeThink,
        int numPredict,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = JsonValue.Create(_model),
            ["stream"] = JsonValue.Create(false),
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = JsonValue.Create("system"),
                    ["content"] = JsonValue.Create(prompt.System)
                },
                new JsonObject
                {
                    ["role"] = JsonValue.Create("user"),
                    ["content"] = JsonValue.Create(prompt.User)
                }
            },
            ["options"] = new JsonObject
            {
                ["temperature"] = JsonValue.Create(0.2),
                ["num_predict"] = JsonValue.Create(numPredict)
            }
        };

        if (useSchema)
            payload["format"] = JsonNode.Parse(schema.GetRawText());
        else
            payload["format"] = JsonValue.Create("json");

        if (includeThink)
            payload["think"] = JsonValue.Create(false);

        var json = payload.ToJsonString();
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Post, "api/chat", content, _generateTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> ReadChatContentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new OllamaRequestException(AiFailureKind.ModelMissing);

        if ((int)response.StatusCode is 500 or 503)
        {
            throw new OllamaRequestException(LooksLikeResourcePressure(body)
                ? AiFailureKind.InsufficientResources
                : AiFailureKind.Unexpected);
        }

        if (!response.IsSuccessStatusCode)
            throw new OllamaRequestException(AiFailureKind.Unexpected);

        try
        {
            if (!TryReadAssistantContent(body, out var content, out var failure, out var stage))
            {
                if (failure == AiFailureKind.InvalidJson)
                    throw new OllamaRequestException(failure, stage);

                return string.Empty;
            }

            return content;
        }
        catch (OllamaRequestException)
        {
            throw;
        }
        catch (JsonException)
        {
            AiDiagnostics.Fail("ollama-envelope", "The Ollama response was not valid JSON.");
            throw new OllamaRequestException(AiFailureKind.InvalidJson, "ollama-envelope");
        }
    }

    public static bool TryReadAssistantContent(string? body, out string content, out AiFailureKind failure, out string stage)
    {
        content = string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            failure = AiFailureKind.EmptyResponse;
            stage = "ollama-envelope";
            return false;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<OllamaChatEnvelope>(body, AiChecklistJson.SerializerOptions);
            var text = envelope?.Message?.Content;
            if (string.IsNullOrWhiteSpace(text))
                text = envelope?.Message?.Thinking;
            if (string.IsNullOrWhiteSpace(text))
            {
                failure = AiFailureKind.EmptyResponse;
                stage = "message-content";
                return false;
            }

            content = text;
            failure = AiFailureKind.InvalidJson;
            stage = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            failure = AiFailureKind.InvalidJson;
            stage = "ollama-envelope";
            return false;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUrl,
        HttpContent? content,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        EnsureLoopback();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var request = new HttpRequestMessage(method, relativeUrl) { Content = content };
        try
        {
            return await _http.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OllamaRequestException(AiFailureKind.Cancelled);
        }
        catch (OperationCanceledException)
        {
            throw new OllamaRequestException(AiFailureKind.Timeout);
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex))
        {
            throw new OllamaRequestException(AiFailureKind.ConnectionRefused);
        }
        catch (HttpRequestException)
        {
            throw new OllamaRequestException(AiFailureKind.NotRunning);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream)
        {
            using var reader = new StreamReader(stream);
            var buffer = new char[4096];
            var builder = new StringBuilder();
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                if (builder.Length + read > MaxResponseCharacters)
                    throw new OllamaRequestException(AiFailureKind.Unexpected);

                builder.Append(buffer, 0, read);
            }

            return builder.ToString();
        }
    }

    private static IReadOnlyList<string> ParseModelNames(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (!document.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
                return [];

            var names = new List<string>();
            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object)
                    continue;

                AddName(names, ReadJsonString(model, "name"));
                AddName(names, ReadJsonString(model, "model"));
            }

            return names;
        }
        catch (JsonException)
        {
            throw new OllamaRequestException(AiFailureKind.Unexpected);
        }
    }

    private static void AddName(List<string> names, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        var trimmed = name.Trim();
        if (names.Exists(existing => existing.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            return;

        names.Add(trimmed);
    }

    private static string? ReadJsonString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return value.GetString();
    }

    private static bool LooksLikeResourcePressure(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        return body.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
            || body.Contains("insufficient memory", StringComparison.OrdinalIgnoreCase)
            || body.Contains("cannot allocate", StringComparison.OrdinalIgnoreCase)
            || body.Contains("resource exhausted", StringComparison.OrdinalIgnoreCase)
            || body.Contains("system memory", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConnectionRefused(Exception exception)
    {
        for (var current = exception as Exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionRefused)
                return true;
        }

        return false;
    }
}
