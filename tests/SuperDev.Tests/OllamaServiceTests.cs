using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using SuperDev.Models;
using SuperDev.Services.Ai;

namespace SuperDev.Tests;

public class OllamaServiceTests
{
    [Fact]
    public void Default_endpoint_is_loopback_only()
    {
        Assert.True(OllamaService.LocalEndpoint.IsLoopback);
        Assert.Equal(11434, OllamaService.LocalEndpoint.Port);
        Assert.Equal("qwen3.5:0.8b", OllamaService.DefaultModel);
    }

    [Fact]
    public async Task Connection_refused_is_reported_without_the_exception_text()
    {
        using var service = Create(_ => throw new HttpRequestException(
            "No connection could be made because the target machine actively refused it.",
            new SocketException((int)SocketError.ConnectionRefused)));

        var availability = await service.CheckAvailabilityAsync();

        Assert.Equal(OllamaReachability.ConnectionRefused, availability.Reachability);
    }

    [Fact]
    public async Task Missing_model_does_not_call_chat()
    {
        var chats = 0;
        using var service = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("chat", StringComparison.Ordinal))
            {
                chats++;
                return Task.FromResult(Json(HttpStatusCode.OK, """{"message":{"content":"{}"}}"""));
            }

            return Task.FromResult(Json(HttpStatusCode.OK, """{"models":[{"name":"llama3.2"}]}"""));
        });

        var available = await service.IsModelAvailableAsync(OllamaService.DefaultModel);
        var result = await service.GenerateChecklistAsync(SampleRequest());

        Assert.False(available);
        Assert.Equal(0, chats);
        Assert.Equal(AiFailureKind.ModelMissing, result.Failure!.Kind);
        Assert.Contains("Choose Generate", result.Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_sends_a_local_chat_request_and_parses_json()
    {
        string? body = null;
        using var service = Create(async request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("tags", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"models":[{"name":"Qwen3.5:0.8B"}]}""");

            Assert.True(request.RequestUri.IsLoopback);
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """
                {"message":{"content":"{\"title\":\"MAUI Android Bug Fix\",\"summary\":\"Diagnose the crash.\",\"sections\":[{\"name\":\"Investigation\",\"items\":[{\"title\":\"Reproduce the issue\",\"description\":\"Reproduce it on a device.\",\"type\":\"Required\",\"priority\":\"High\",\"acceptanceCriteria\":\"It reproduces.\"}]}]}"}}
                """);
        });

        var result = await service.GenerateChecklistAsync(SampleRequest());

        Assert.True(result.Succeeded);
        Assert.Equal("MAUI Android Bug Fix", result.Checklist!.Title);
        Assert.Equal("Diagnose the crash.", result.Checklist.Summary);
        Assert.NotNull(body);
        Assert.Contains("\"model\":\"qwen3.5:0.8b\"", body, StringComparison.Ordinal);
        Assert.Contains("\"temperature\":0.2", body, StringComparison.Ordinal);
        Assert.Contains("\"think\":false", body, StringComparison.Ordinal);
        Assert.Contains("Crash when rotating", body, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_summary_that_copies_the_task_is_replaced_with_the_checks()
    {
        using var service = Create(Chat("""
            {"message":{"content":"{\"title\":\"Rotation crash\",\"summary\":\"Crash when rotating\",\"sections\":[{\"name\":\"Investigation\",\"items\":[{\"title\":\"Reproduce the issue\",\"description\":\"Reproduce it on a device.\",\"type\":\"Investigation\",\"priority\":\"Required\"}]}]}"}}
            """));

        var result = await service.GenerateChecklistAsync(SampleRequest());

        Assert.True(result.Succeeded);
        Assert.Equal(
            "Investigation: Reproduce the issue — Reproduce it on a device.",
            result.Checklist!.Summary);
    }

    [Fact]
    public async Task Ollama_envelope_is_unwrapped_before_the_checklist_is_read()
    {
        const string checklist = """
            {"title":"Flutter Login Page Implementation Checklist","summary":"A comprehensive checklist.","sections":[{"section_title":"Security & Authentication","items":[{"title":"Implement JWT or OAuth2 authentication","description":"Configure secure token handling.","type":"Implementation","priority":"High"}]}]}
            """;
        var envelope = "{\"model\":\"qwen3.5:0.8b\",\"message\":{\"role\":\"assistant\",\"content\":"
            + System.Text.Json.JsonSerializer.Serialize(checklist)
            + "}}";
        using var service = Create(Chat(envelope));

        var extracted = OllamaService.TryReadAssistantContent(envelope, out var content, out _, out var stage);
        var result = await service.GenerateChecklistAsync(SampleRequest());

        Assert.True(extracted);
        Assert.Equal(string.Empty, stage);
        Assert.Contains("section_title", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"role\"", content, StringComparison.Ordinal);
        Assert.True(result.Succeeded);
        Assert.Equal("Flutter Login Page Implementation Checklist", result.Checklist!.Title);
        Assert.Equal("Security & Authentication", result.Checklist.Sections[0].Name);
        Assert.Equal("Implementation", result.Checklist.Sections[0].Items[0].Type);
        Assert.Equal("Critical", result.Checklist.Sections[0].Items[0].Priority);
    }

    [Fact]
    public async Task Generate_check_sends_one_item_request_and_parses_it()
    {
        string? body = null;
        using var service = Create(async request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("tags", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"models":[{"name":"qwen3.5:0.8b"}]}""");

            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """
                {"message":{"content":"{\"title\":\"Reproduce the crash\",\"category\":\"Understand\",\"priority\":\"Critical\",\"description\":\"Rotate the screen.\",\"acceptanceCriteria\":\"It crashes again.\",\"subtasks\":[\"Open the screen\",\"Rotate it\"]}"}}
                """);
        });

        var result = await service.GenerateCheckAsync(new CheckGenerationRequest
        {
            ChecklistTitle = "Login crash",
            WorkType = WorkType.Bug,
            DraftTitle = "Reproduce the crash"
        });

        Assert.True(result.Succeeded);
        Assert.Equal("Reproduce the crash", result.Check!.Title);
        Assert.Equal("Understand", result.Check.Category);
        Assert.Equal(["Open the screen", "Rotate it"], result.Check.Subtasks);
        Assert.NotNull(body);
        Assert.Contains("\"num_predict\":1024", body, StringComparison.Ordinal);
        Assert.Contains("Login crash", body, StringComparison.Ordinal);
        Assert.Contains("exactly one", body, StringComparison.Ordinal);
        Assert.DoesNotContain("6 to 12", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_model_does_not_generate_a_check()
    {
        var chats = 0;
        using var service = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("chat", StringComparison.Ordinal))
                chats++;

            return Task.FromResult(Json(HttpStatusCode.OK, """{"models":[{"name":"llama3.2"}]}"""));
        });

        var result = await service.GenerateCheckAsync(new CheckGenerationRequest
        {
            ChecklistTitle = "Login crash",
            WorkType = WorkType.Bug
        });

        Assert.Equal(0, chats);
        Assert.Equal(AiFailureKind.ModelMissing, result.Failure!.Kind);
    }

    [Fact]
    public async Task Invalid_and_empty_chat_content_are_rejected()
    {
        using var invalid = Create(Chat("""{"message":{"content":"not json"}}"""));
        using var empty = Create(Chat("""{"message":{"content":"  "}}"""));

        var invalidResult = await invalid.GenerateChecklistAsync(SampleRequest());
        var emptyResult = await empty.GenerateChecklistAsync(SampleRequest());

        Assert.Equal(AiFailureKind.InvalidJson, invalidResult.Failure!.Kind);
        Assert.Equal(AiFailureKind.EmptyResponse, emptyResult.Failure!.Kind);
        Assert.DoesNotContain("not json", invalidResult.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resource_errors_hide_the_response_body()
    {
        using var service = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("tags", StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, """{"models":[{"name":"qwen3.5:0.8b"}]}"""));

            return Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "model requires more system memory"));
        });

        var result = await service.GenerateChecklistAsync(SampleRequest());

        Assert.Equal(AiFailureKind.InsufficientResources, result.Failure!.Kind);
        Assert.DoesNotContain("system memory", result.Failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_stops_generation()
    {
        using var service = Create(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("tags", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"models":[{"name":"qwen3.5:0.8b"}]}""");

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Json(HttpStatusCode.OK, "{}");
        });
        using var cancellation = new CancellationTokenSource();

        var pending = service.GenerateChecklistAsync(SampleRequest(), cancellation.Token);
        cancellation.Cancel();
        var result = await pending;

        Assert.Equal(AiFailureKind.Cancelled, result.Failure!.Kind);
        Assert.Equal("Generation was cancelled.", result.Failure.Message);
    }

    [Fact]
    public async Task Generation_timeout_is_distinct_from_cancellation()
    {
        using var service = Create(
            async (request, cancellationToken) =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("tags", StringComparison.Ordinal))
                    return Json(HttpStatusCode.OK, """{"models":[{"name":"qwen3.5:0.8b"}]}""");

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                return Json(HttpStatusCode.OK, "{}");
            },
            generateTimeout: TimeSpan.FromMilliseconds(40));

        var result = await service.GenerateChecklistAsync(SampleRequest());

        Assert.Equal(AiFailureKind.Timeout, result.Failure!.Kind);
    }

    [Fact]
    public async Task A_non_loopback_client_is_rejected_before_the_request()
    {
        var calls = 0;
        var handler = new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Json(HttpStatusCode.OK, """{"models":[]}"""));
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.com") };
        using var service = new OllamaService(client);

        var availability = await service.CheckAvailabilityAsync();

        Assert.Equal(OllamaReachability.Unavailable, availability.Reachability);
        Assert.Equal(0, calls);
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Chat(string body) =>
        (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.Contains("tags", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, """{"models":[{"name":"qwen3.5:0.8b"}]}""")
            : Json(HttpStatusCode.OK, body));

    private static OllamaService Create(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send,
        TimeSpan? generateTimeout = null) =>
        Create((request, _) => send(request), generateTimeout);

    private static OllamaService Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        TimeSpan? generateTimeout = null)
    {
        var handler = new StubHandler(send);
        var client = new HttpClient(handler)
        {
            BaseAddress = OllamaService.LocalEndpoint,
            Timeout = Timeout.InfiniteTimeSpan
        };
        return new OllamaService(
            client,
            new PromptBuilder(),
            new ChecklistValidator(),
            OllamaService.DefaultModel,
            TimeSpan.FromSeconds(2),
            generateTimeout ?? TimeSpan.FromSeconds(5));
    }

    private static AiChecklistRequest SampleRequest() => new()
    {
        Platform = DevPlatform.Android,
        Framework = DevFramework.Maui,
        WorkKind = AiWorkKind.BugFix,
        Description = "Crash when rotating"
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
