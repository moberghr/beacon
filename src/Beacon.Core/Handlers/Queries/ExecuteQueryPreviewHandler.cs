using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Queries;

internal sealed class ExecuteQueryPreviewHandler(IQueryExecutionPreviewService previewService)
    : IRequestHandler<ExecuteQueryPreviewCommand, QueryPreviewResult>
{
    public async Task<QueryPreviewResult> Handle(ExecuteQueryPreviewCommand request, CancellationToken cancellationToken)
    {
        var result = await previewService.ExecuteQueryPreview(request.QueryId, request.Draft, request, cancellationToken);

        if (result == null)
        {
            throw new InvalidOperationException($"Query #{request.QueryId} preview failed.");
        }

        return result;
    }
}

/// <summary>Runs the query and returns one page of its result; <c>sort</c> names a result column.</summary>
public record ExecuteQueryPreviewCommand : ListRequest, IRequest<QueryPreviewResult>, IProjectAuthorizedRequest
{
    public required int QueryId { get; init; }

    /// <summary>The editor's unsaved steps; when set they run instead of the stored query, which stays untouched.</summary>
    public QueryDraft? Draft { get; init; }

    // A stored query runs (Run); a draft runs the editor's own SQL on its steps' data sources (Query there, and reading
    // the query it edits).
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
        Draft == null
            ? [new(ProjectResource.Query(QueryId), ProjectAction.Run)]
            :
            [
                new(ProjectResource.Query(QueryId), ProjectAction.Read),
                new(ProjectResource.DataSources(Draft.Steps?.Select(x => x.DataSourceId)), ProjectAction.Query)
            ];
}
