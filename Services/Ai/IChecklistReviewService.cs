using System.Threading;
using System.Threading.Tasks;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public interface IChecklistReviewService
{
    Task<ChecklistReviewOutcome> ReviewAsync(
        ChecklistReviewRequest request,
        CancellationToken cancellationToken = default);
}
