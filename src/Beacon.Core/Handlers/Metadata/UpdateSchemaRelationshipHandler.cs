using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services.Metadata;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Metadata;

internal sealed class UpdateSchemaRelationshipHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ISchemaGraphService schemaGraphService)
    : IRequestHandler<UpdateSchemaRelationshipCommand>
{
    public async Task Handle(UpdateSchemaRelationshipCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The relationship must belong to the route's data source, the one the caller's access was checked on.
        var relationship = await context.SchemaRelationships
            .Where(x => x.Id == request.Id)
            .Where(x => x.DataSourceId == request.DataSourceId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Schema relationship {request.Id} not found.");

        if (!string.IsNullOrWhiteSpace(request.Label))
        {
            relationship.Label = request.Label;
        }

        relationship.Cardinality = request.Cardinality;

        await context.SaveChangesAsync(cancellationToken);

        schemaGraphService.Invalidate(relationship.DataSourceId);
    }
}

/// <summary>Relabels a relationship of the data source <c>DataSourceId</c>; 404 when the relationship is not that source's.</summary>
public record UpdateSchemaRelationshipCommand : IRequest, IProjectAuthorizedRequest
{
    public int DataSourceId { get; init; }

    [ProjectScopeExempt("bound to the route's data source by the handler (404 when it is another source's)")]
    public int Id { get; init; }

    public string? Label { get; init; }

    public SchemaRelationshipCardinality Cardinality { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        ProjectCheck.SharedSource(ProjectResource.DataSource(DataSourceId), ProjectAction.Write)
    ];
}
