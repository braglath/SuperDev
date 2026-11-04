using System;
using System.Threading;
using System.Threading.Tasks;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public static class ChecklistReviewMessages
{
    public static string For(AiFailureKind kind) => kind switch
    {
        AiFailureKind.NotInstalled =>
            "Local AI is not set up yet. Super Dev can download it onto this computer.",
        AiFailureKind.NotRunning =>
            "Ollama is installed but not running. Start local AI and try the review again.",
        AiFailureKind.ConnectionRefused =>
            "Super Dev could not connect to the local AI service. Start it and try the review again.",
        AiFailureKind.ModelMissing =>
            "The local model is not on this computer yet. Download " + AIModelManager.DefaultModel + " and try again.",
        AiFailureKind.Timeout =>
            "The review took too long. Try again.",
        AiFailureKind.Cancelled =>
            "Review was cancelled.",
        AiFailureKind.InvalidJson =>
            "The model returned a response that could not be read. Try the review again.",
        AiFailureKind.EmptyResponse =>
            "The model returned an empty review. Try again.",
        AiFailureKind.InvalidChecklist =>
            "The review could not be validated. Try again.",
        AiFailureKind.InsufficientResources =>
            "Ollama could not finish the review. This machine may be low on memory. Close other apps and try again.",
        _ =>
            "The review failed. Check that Ollama is running and try again."
    };
}

public sealed class ChecklistReviewService : IChecklistReviewService
{
    private readonly OllamaService _ollama;
    private readonly ChecklistReviewPromptBuilder _prompts;
    private readonly ChecklistReviewValidator _validator;

    public ChecklistReviewService(OllamaService ollama)
        : this(ollama, new ChecklistReviewPromptBuilder(), new ChecklistReviewValidator())
    {
    }

    public ChecklistReviewService(
        OllamaService ollama,
        ChecklistReviewPromptBuilder prompts,
        ChecklistReviewValidator validator)
    {
        _ollama = ollama ?? throw new ArgumentNullException(nameof(ollama));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<ChecklistReviewOutcome> ReviewAsync(
        ChecklistReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var prompt = _prompts.Build(request);
            var content = await _ollama.RequestStructuredAsync(
                prompt,
                ChecklistReviewValidator.Schema,
                numPredict: 2048,
                cancellationToken).ConfigureAwait(false);
            return FromInspection(_validator.Read(content, request));
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

    private static ChecklistReviewOutcome FromInspection(ChecklistReviewInspection inspection)
    {
        if (!inspection.IsValid || inspection.Review is null)
            return Fail(inspection.FailureKind ?? AiFailureKind.InvalidChecklist);

        return new ChecklistReviewOutcome
        {
            Review = inspection.Review,
            Warnings = inspection.Warnings
        };
    }

    private static ChecklistReviewOutcome Fail(AiFailureKind kind) => new()
    {
        Failure = new AiFailure
        {
            Kind = kind,
            Message = ChecklistReviewMessages.For(kind)
        }
    };
}
