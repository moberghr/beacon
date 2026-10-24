using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.Queries;

internal sealed class ExecuteQueryPreviewHandler(
    IQueryService queryService,
    IStoredQueryPreview storedPreview,
    ProjectRequestAuthorizer authorizer,
    ILogger<ExecuteQueryPreviewHandler> logger)
    : IRequestHandler<ExecuteQueryPreviewCommand, QueryPreviewResult>
{
    public async Task<QueryPreviewResult> Handle(ExecuteQueryPreviewCommand request, CancellationToken cancellationToken)
    {
        var result = await RunAsync(queryService, storedPreview, authorizer, logger, request.QueryId, request.Draft, request, cancellationToken);

        if (result == null)
        {
            throw new InvalidOperationException($"Query #{request.QueryId} preview failed.");
        }

        return result;
    }

    /// <summary>
    /// One page of a query's result (the saved query, or the draft when given); a failure comes back as the result,
    /// with its cause, a cancellation as null. Shared with the obsolete service forwarder.
    /// </summary>
    internal static async Task<QueryPreviewResult?> RunAsync(
        IQueryService queryService,
        IStoredQueryPreview storedPreview,
        ProjectRequestAuthorizer authorizer,
        ILogger logger,
        int queryId,
        QueryDraft? draft,
        ListRequest paging,
        CancellationToken cancellationToken)
    {
        await PreviewRunSupport.DemandMayRunAsync(authorizer, queryId, draft, cancellationToken);

        try
        {
            if (draft != null)
            {
                return await queryService.PreviewQuery(queryId, draft, paging, cancellationToken);
            }

            var snapshot = await PreviewRunSupport.LoadRunnableSnapshotAsync(storedPreview, authorizer, queryId, cancellationToken);

            return await storedPreview.PreviewSnapshot(snapshot, paging, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Query preview for query {QueryId} was cancelled", queryId);
            return null;
        }
        catch (Exception ex) when (!PreviewRunSupport.IsAccessRefusal(ex))
        {
            // The type and error code only (§1.11): the message can quote the SQL or row values.
            logger.LogWarning(
                "Error executing query preview for query {QueryId}: {Failure}",
                queryId,
                FailureCodes.Describe(ex));
            return PreviewRunSupport.Failed(ex);
        }
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
