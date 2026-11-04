using SuperDev.Models;
using SuperDev.Services.Ai;

namespace SuperDev.Tests;

public class AIModelManagerTests
{
    [Fact]
    public async Task Reports_not_installed_when_nothing_is_listening_and_the_binary_is_missing()
    {
        var ai = new FakeAi { Availability = new OllamaAvailability(OllamaReachability.ConnectionRefused) };
        var manager = new AIModelManager(ai, () => false);

        var status = await manager.DetectAsync();

        Assert.Equal(OllamaStatus.NotInstalled, status.Status);
        Assert.False(status.CanGenerate);
        Assert.Contains("Generate with AI", status.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_not_running_when_ollama_is_installed_but_refuses_the_connection()
    {
        var ai = new FakeAi { Availability = new OllamaAvailability(OllamaReachability.ConnectionRefused) };
        var manager = new AIModelManager(ai, () => true);

        var status = await manager.DetectAsync();

        Assert.Equal(OllamaStatus.NotRunning, status.Status);
        Assert.Equal("Ollama disconnected", status.ConnectionText);
        Assert.Contains("not running", status.Guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reports_a_missing_model_without_downloading_it()
    {
        var ai = new FakeAi
        {
            Availability = new OllamaAvailability(OllamaReachability.Reachable),
            ModelAvailable = false
        };
        var manager = new AIModelManager(ai, () => true);

        var status = await manager.DetectAsync();

        Assert.Equal(OllamaStatus.ModelMissing, status.Status);
        Assert.Equal("Ollama connected", status.ConnectionText);
        Assert.Equal("Model missing", status.ModelStateText);
        Assert.Contains("Generate with AI", status.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_ready_when_the_selected_model_is_installed()
    {
        var ai = new FakeAi
        {
            Availability = new OllamaAvailability(OllamaReachability.Reachable),
            ModelAvailable = true
        };
        var manager = new AIModelManager(ai, () => false);

        var status = await manager.DetectAsync();

        Assert.Equal(OllamaStatus.Ready, status.Status);
        Assert.True(status.CanGenerate);
        Assert.Equal("qwen3.5:0.8b", status.ModelName);
        Assert.Equal("Model installed", status.ModelStateText);
    }

    [Fact]
    public async Task A_failed_model_check_stays_unavailable()
    {
        var ai = new FakeAi
        {
            Availability = new OllamaAvailability(OllamaReachability.Reachable),
            ModelException = new OllamaRequestException(AiFailureKind.Unexpected)
        };
        var manager = new AIModelManager(ai, () => true);

        var status = await manager.DetectAsync();

        Assert.Equal(OllamaStatus.Unavailable, status.Status);
        Assert.False(status.CanGenerate);
    }

    private sealed class FakeAi : ILocalAIService
    {
        public OllamaAvailability Availability { get; init; } = new(OllamaReachability.Reachable);

        public bool ModelAvailable { get; init; }

        public Exception? ModelException { get; init; }

        public Task<OllamaAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Availability);

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(ModelAvailable ? [AIModelManager.DefaultModel] : []);

        public Task<bool> IsModelAvailableAsync(string modelName, CancellationToken cancellationToken = default)
        {
            if (ModelException is not null)
                return Task.FromException<bool>(ModelException);

            return Task.FromResult(ModelAvailable && OllamaService.ModelsMatch(AIModelManager.DefaultModel, modelName));
        }

        public Task<AiGenerationResult> GenerateChecklistAsync(
            AiChecklistRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiGenerationResult> GenerateSectionAsync(
            AiChecklistRequest request,
            string sectionName,
            IReadOnlyList<string> existingTitles,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiGenerationResult> GenerateCheckAsync(
            CheckGenerationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
