using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Services.Metadata;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Metadata;

internal sealed class DeleteSchemaRelationshipHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ISchemaGraphService schemaGraphService)
    : IRequestHandler<DeleteSchemaRelationshipCommand>
{
    public async Task Handle(DeleteSchemaRelationshipCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The relationship must belong to the route's data source, the one the caller's access was checked on.
        var relationship = await context.SchemaRelationships
            .Where(x => x.Id == request.Id)
            .Where(x => x.DataSourceId == request.DataSourceId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Schema relationship {request.Id} not found.");

        // Soft delete (§2.14) — the global query filter takes it out of the graph, and re-running sync
        // will not resurrect a foreign-key edge a user deliberately removed.
        relationship.Archive();

        await context.SaveChangesAsync(cancellationToken);

        schemaGraphService.Invalidate(relationship.DataSourceId);
    }
}

/// <summary>Removes a relationship of the data source <c>DataSourceId</c>; 404 when it is not that source's.</summary>
public record DeleteSchemaRelationshipCommand(
    int DataSourceId,
    [property: ProjectScopeExempt("bound to the route's data source by the handler (404 when it is another source's)")] int Id) : IRequest, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        ProjectCheck.SharedSource(ProjectResource.DataSource(DataSourceId), ProjectAction.Manage)
    ];
}
