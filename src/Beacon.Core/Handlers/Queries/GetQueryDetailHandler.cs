using Beacon.Core.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.SavedQueries;

namespace Beacon.Core.Handlers.Queries;

internal sealed class GetQueryDetailHandler(
    IQueryService queryService,
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetQueryDetailQuery, QueryDetailsData>
{
    internal const string UnreadableRunnableVersionIssue = "Its approved version reads data sources you cannot read.";

    public async Task<QueryDetailsData> Handle(GetQueryDetailQuery request, CancellationToken cancellationToken)
    {
        var details = await queryService.GetQueryDetails(request.QueryId, cancellationToken);

        if (details == null)
        {
            throw new InvalidOperationException($"Query #{request.QueryId} not found.");
        }

        // The version an MCP tool runs is described only to a caller who may read that version, as its own detail is
        // decided: the query's live steps can have left the data sources the version recorded.
        if (details.McpToolRunnableVersionNumber != null && !await MayReadRunnableVersionAsync(request.QueryId, cancellationToken))
        {
            details.McpToolRunnableVersionNumber = null;
            details.McpToolIssue = UnreadableRunnableVersionIssue;
        }

        return details;
    }

    private async Task<bool> MayReadRunnableVersionAsync(int queryId, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var readable = scope.For(ProjectAction.Read);
        if (readable.Ids == null)
        {
            return true;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var version = await context.Queries
            .Where(x => x.Id == queryId)
            .WhereHasRunnableVersion(context)
            .Select(x =>
                new
                {
                    x.ActiveVersion!.DataSourceIds,
                    StepsJson = x.ActiveVersion.DataSourceIds == null ? x.ActiveVersion.StepsJson : null,
                    x.OwnerUserId
                })
            .FirstOrDefaultAsync(cancellationToken);
        if (version == null)
        {
            return false;
        }

        var sources = QueryVersionSources.Of(version.DataSourceIds, version.StepsJson);
        var reachable = sources is { Count: > 0 } && await ProjectSourceFootprint.AllowsAsync(context, readable, sources, cancellationToken);

        return QueryVersionSources.IsReadable(readable, sources, reachable, version.OwnerUserId);
    }
}

public record GetQueryDetailQuery : IRequest<QueryDetailsData>, IProjectAuthorizedRequest
{
    public required int QueryId { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Query(QueryId), ProjectAction.Read)
    ];
}
