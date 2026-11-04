using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public interface ILocalAIService
{
    Task<OllamaAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);

    Task<bool> IsModelAvailableAsync(string modelName, CancellationToken cancellationToken = default);

    Task<AiGenerationResult> GenerateChecklistAsync(
        AiChecklistRequest request,
        CancellationToken cancellationToken = default);

    Task<AiGenerationResult> GenerateSectionAsync(
        AiChecklistRequest request,
        string sectionName,
        IReadOnlyList<string> existingTitles,
        CancellationToken cancellationToken = default);

    Task<AiGenerationResult> GenerateCheckAsync(
        CheckGenerationRequest request,
        CancellationToken cancellationToken = default);
}
