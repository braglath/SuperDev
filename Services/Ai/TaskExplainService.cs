using System;
using System.Threading;
using System.Threading.Tasks;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public static class TaskExplainMessages
{
    public static string For(AiFailureKind kind) => kind switch
    {
        AiFailureKind.NotInstalled =>
            "Local AI is not set up yet. Super Dev can download it onto this computer.",
        AiFailureKind.NotRunning =>
            "Ollama is installed but not running. Start local AI and try again.",
        AiFailureKind.ConnectionRefused =>
            "Super Dev could not connect to the local AI service. Start it and try again.",
        AiFailureKind.ModelMissing =>
            "The local model is not on this computer yet. Download " + AIModelManager.DefaultModel + " and try again.",
        AiFailureKind.Timeout =>
            "The explanation took too long. Try again.",
        AiFailureKind.Cancelled =>
            "Explanation was cancelled.",
        AiFailureKind.InvalidJson =>
            "The model returned a response that could not be read. Try again.",
        AiFailureKind.EmptyResponse =>
            "The model returned an empty explanation. Try again.",
        AiFailureKind.InvalidChecklist =>
            "The explanation could not be read. Try again.",
        AiFailureKind.InsufficientResources =>
            "Ollama could not finish the explanation. This machine may be low on memory. Close other apps and try again.",
        _ =>
            "The explanation failed. Check that Ollama is running and try again."
    };
}

public sealed class TaskExplainService
{
    private readonly OllamaService _ollama;
    private readonly TaskExplainPromptBuilder _prompts;
    private readonly TaskExplainValidator _validator;

    public TaskExplainService(OllamaService ollama)
        : this(ollama, new TaskExplainPromptBuilder(), new TaskExplainValidator())
    {
    }

    public TaskExplainService(
        OllamaService ollama,
        TaskExplainPromptBuilder prompts,
        TaskExplainValidator validator)
    {
        _ollama = ollama ?? throw new ArgumentNullException(nameof(ollama));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<TaskExplainOutcome> ExplainAsync(
        TaskExplainRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var prompt = _prompts.Build(request);
            var content = await _ollama.RequestStructuredAsync(
                prompt,
                TaskExplainValidator.Schema,
                numPredict: 1024,
                cancellationToken).ConfigureAwait(false);
            return FromInspection(_validator.Read(content));
        }
        catch (OllamaRequestException ex)
        {
            AiDiagnostics.Fail(ex.Stage, ex.Message);
            return Fail(ex.Kind);
        }
        catch (OperationCanceledException)
        {
            return Fail(AiFailureKind.Cancelled);
        }
    }

    private static TaskExplainOutcome FromInspection(TaskExplainInspection inspection)
    {
        if (!inspection.IsValid || inspection.Explanation is null)
            return Fail(inspection.FailureKind ?? AiFailureKind.InvalidChecklist);

        return new TaskExplainOutcome
        {
            Explanation = inspection.Explanation,
            Warnings = inspection.Warnings
        };
    }

    private static TaskExplainOutcome Fail(AiFailureKind kind) => new()
    {
        Failure = new AiFailure
        {
            Kind = kind,
            Message = TaskExplainMessages.For(kind)
        }
    };
}
