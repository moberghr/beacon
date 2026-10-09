using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Queries;

internal sealed class ExecuteStepPreviewHandler(IQueryExecutionPreviewService previewService)
    : IRequestHandler<ExecuteStepPreviewCommand, QueryPreviewResult>
{
    public async Task<QueryPreviewResult> Handle(ExecuteStepPreviewCommand request, CancellationToken cancellationToken)
    {
        var result = await previewService.ExecuteStepPreview(
            request.QueryId,
            request.StepOrder,
            request.Parameters,
            request.Draft,
            request,
            cancellationToken);

        if (result == null)
        {
            throw new InvalidOperationException(
                $"Step preview failed for query #{request.QueryId} step {request.StepOrder}.");
        }

        return result;
    }
}

/// <summary>Runs one step on its own and returns one page of its result; <c>sort</c> names a result column.</summary>
public record ExecuteStepPreviewCommand : ListRequest, IRequest<QueryPreviewResult>
{
    public required int QueryId { get; init; }

    public required int StepOrder { get; init; }

    public List<ParameterValue>? Parameters { get; init; }

    /// <summary>The editor's unsaved steps; when set the step runs from here instead of the stored query.</summary>
    public QueryDraft? Draft { get; init; }
}
