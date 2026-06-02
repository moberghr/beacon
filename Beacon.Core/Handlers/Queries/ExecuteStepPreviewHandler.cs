using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Queries;

internal sealed class ExecuteStepPreviewHandler(IQueryExecutionPreviewService previewService)
    : IRequestHandler<ExecuteStepPreviewCommand, QueryStepResult>
{
    public async Task<QueryStepResult> Handle(ExecuteStepPreviewCommand request, CancellationToken cancellationToken)
    {
        var result = await previewService.ExecuteStepPreview(
            request.QueryId,
            request.StepOrder,
            request.Parameters,
            cancellationToken);

        if (result == null)
        {
            throw new InvalidOperationException(
                $"Step preview failed for query #{request.QueryId} step {request.StepOrder}.");
        }

        return result;
    }
}

public record ExecuteStepPreviewCommand : IRequest<QueryStepResult>
{
    public required int QueryId { get; init; }

    public required int StepOrder { get; init; }

    public List<ParameterValue>? Parameters { get; init; }
}
