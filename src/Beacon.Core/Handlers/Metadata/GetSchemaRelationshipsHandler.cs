using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Core.Handlers.Metadata;

/// <summary>
/// A data source's relationships, each with whether the caller may verify it or take its verification back
/// (<see cref="GroundingCuration"/>, as the verify route decides: the same for every relationship of the source).
/// </summary>
internal sealed class GetSchemaRelationshipsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IBeaconPermissionAccessor permissionAccessor)
    : IRequestHandler<GetSchemaRelationshipsQuery, PagedList<SchemaRelationshipItem>>
{
    public async Task<PagedList<SchemaRelationshipItem>> Handle(GetSchemaRelationshipsQuery request, CancellationToken cancellationToken)
    {
        if (request.DataSourceId <= 0)
        {
            throw new InvalidOperationException("Data source id must be positive.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var relationships = context.SchemaRelationships
            .AsNoTracking()
            .Where(x => x.DataSourceId == request.DataSourceId);

        if (request.Origin != null)
        {
            relationships = relationships.Where(x => x.Origin == request.Origin);
        }

        if (request.VerifiedOnly == true)
        {
            relationships = relationships.Where(x => x.IsVerified);
        }

        var page = await relationships
            .Select(x =>
                new SchemaRelationshipItem
                {
                    Id = x.Id,
                    SourceSchema = x.SourceSchema,
                    SourceTable = x.SourceTable,
                    SourceColumn = x.SourceColumn,
                    TargetSchema = x.TargetSchema,
                    TargetTable = x.TargetTable,
                    TargetColumn = x.TargetColumn,
                    Label = x.Label,
                    Origin = x.Origin,
                    Cardinality = x.Cardinality,
                    Confidence = x.Confidence,
                    IsVerified = x.IsVerified,
                    VerifiedTime = x.VerifiedTime
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "sourceSchema,sourceTable,sourceColumn");
        if (page.Items.Count == 0)
        {
            return page;
        }

        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var shared = await GroundingCuration.OfSharedSourcesAsync(context, scope, permissionAccessor, [request.DataSourceId], cancellationToken);
        var canCurate = shared.Allows(request.DataSourceId, shared.WritesSource(request.DataSourceId));

        return page.Map(x => x with { CanCurate = canCurate });
    }
}

/// <summary>
/// A data source's relationships, ordered by source schema, table and column unless <c>sort</c> says
/// otherwise. <c>DataSourceId</c> binds from the route.
/// </summary>
public record GetSchemaRelationshipsQuery : ListRequest, IRequest<PagedList<SchemaRelationshipItem>>, IProjectAuthorizedRequest
{
    public int DataSourceId { get; init; }

    public SchemaRelationshipOrigin? Origin { get; init; }

    public bool? VerifiedOnly { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.DataSource(DataSourceId), ProjectAction.Read)
    ];
}

public record SchemaRelationshipItem
{
    public int Id { get; init; }
    public required string SourceSchema { get; init; }
    public required string SourceTable { get; init; }
    public required string SourceColumn { get; init; }
    public required string TargetSchema { get; init; }
    public required string TargetTable { get; init; }
    public required string TargetColumn { get; init; }
    public required string Label { get; init; }
    public SchemaRelationshipOrigin Origin { get; init; }
    public SchemaRelationshipCardinality Cardinality { get; init; }
    public double Confidence { get; init; }
    public bool IsVerified { get; init; }
    public DateTime? VerifiedTime { get; init; }

    /// <summary>
    /// Whether the caller may verify the relationship or take its verification back: Curate in every live project linking
    /// the data source, or the DataSourceAdmin permission.
    /// </summary>
    public bool CanCurate { get; init; }
}
