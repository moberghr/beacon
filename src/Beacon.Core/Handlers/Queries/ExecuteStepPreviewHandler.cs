using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.Queries;

internal sealed class ExecuteStepPreviewHandler(
    IQueryService queryService,
    IStoredQueryPreview storedPreview,
    ProjectRequestAuthorizer authorizer,
    ILogger<ExecuteStepPreviewHandler> logger)
    : IRequestHandler<ExecuteStepPreviewCommand, QueryPreviewResult>
{
    public async Task<QueryPreviewResult> Handle(ExecuteStepPreviewCommand request, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            queryService,
            storedPreview,
            authorizer,
            logger,
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

    /// <summary>
    /// One page of a single step's result, run on its own; from the draft when given. A failure comes back as the
    /// result, a cancellation as null. Shared with the obsolete service forwarder.
    /// </summary>
    internal static async Task<QueryPreviewResult?> RunAsync(
        IQueryService queryService,
        IStoredQueryPreview storedPreview,
        ProjectRequestAuthorizer authorizer,
        ILogger logger,
        int queryId,
        int stepOrder,
        List<ParameterValue>? parameters,
        QueryDraft? draft,
        ListRequest paging,
        CancellationToken cancellationToken)
    {
        await PreviewRunSupport.DemandMayRunAsync(authorizer, queryId, draft, cancellationToken);

        try
        {
            if (draft != null)
            {
                return await queryService.PreviewQueryStepPaged(queryId, stepOrder, parameters, draft, paging, cancellationToken);
            }

            var snapshot = await PreviewRunSupport.LoadRunnableSnapshotAsync(storedPreview, authorizer, queryId, cancellationToken);

            return await storedPreview.PreviewSnapshotStep(snapshot, stepOrder, parameters, paging, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Step preview for query {QueryId} step {StepOrder} was cancelled", queryId, stepOrder);
            return null;
        }
        catch (Exception ex) when (!PreviewRunSupport.IsAccessRefusal(ex))
        {
            logger.LogWarning(
                "Error executing step preview for query {QueryId} step {StepOrder}: {Failure}",
                queryId,
                stepOrder,
                FailureCodes.Describe(ex));
            return PreviewRunSupport.Failed(ex);
        }
    }
}

/// <summary>Runs one step on its own and returns one page of its result; <c>sort</c> names a result column.</summary>
public record ExecuteStepPreviewCommand : ListRequest, IRequest<QueryPreviewResult>, IProjectAuthorizedRequest
{
    public required int QueryId { get; init; }

    public required int StepOrder { get; init; }

    public List<ParameterValue>? Parameters { get; init; }

    /// <summary>The editor's unsaved steps; when set the step runs from here instead of the stored query.</summary>
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
