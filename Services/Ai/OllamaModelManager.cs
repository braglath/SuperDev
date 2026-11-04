using System;
using System.Threading;
using System.Threading.Tasks;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public interface IOllamaModelManager
{
    Task PullAsync(string model, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken = default);

    Task<bool> DescribeAsync(string model, CancellationToken cancellationToken = default);

    Task<bool> LoadAsync(string model, CancellationToken cancellationToken = default);
}

public sealed class OllamaModelManager : IOllamaModelManager
{
    private readonly OllamaService _service;

    public OllamaModelManager(OllamaService service) =>
        _service = service ?? throw new ArgumentNullException(nameof(service));

    public Task PullAsync(string model, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken = default) =>
        _service.PullModelAsync(model, progress, cancellationToken);

    public Task<bool> DescribeAsync(string model, CancellationToken cancellationToken = default) =>
        _service.DescribeModelAsync(model, cancellationToken);

    public Task<bool> LoadAsync(string model, CancellationToken cancellationToken = default) =>
        _service.LoadModelAsync(model, cancellationToken);
}
