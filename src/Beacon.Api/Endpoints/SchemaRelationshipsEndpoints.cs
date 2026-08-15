using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Metadata;
using MediatR;

namespace Beacon.Api.Endpoints;

/// <summary>
/// REST surface for registered schema relationships and the schema-health report. One endpoint = one
/// MediatR handler (§2.1.1); endpoints stay thin — resolve path/body, call <c>mediator.Send</c>, return
/// the result. The group inherits the base authenticated <c>BeaconApi</c> policy; relationships are
/// grounding metadata every authenticated user's questions depend on, not an admin-only governance
/// surface like the glossary.
/// </summary>
internal static class SchemaRelationshipsEndpoints
{
    public static RouteGroupBuilder MapSchemaRelationshipsEndpoints(this RouteGroupBuilder group)
    {
        var relationships = group.MapGroup("/data-sources/{dataSourceId:int}").WithTags("SchemaRelationships");

        relationships.MapGet("/relationships",
                (int dataSourceId, SchemaRelationshipOrigin? origin, bool? verifiedOnly, IMediator m, CancellationToken ct) =>
                    m.Send(new GetSchemaRelationshipsQuery(dataSourceId, origin, verifiedOnly ?? false), ct))
            .WithName("GetSchemaRelationships");

        relationships.MapPost("/relationships",
                (int dataSourceId, CreateSchemaRelationshipBody body, IMediator m, CancellationToken ct) =>
                    m.Send(new CreateSchemaRelationshipCommand
                    {
                        DataSourceId = dataSourceId,
                        SourceSchema = body.SourceSchema,
                        SourceTable = body.SourceTable,
                        SourceColumn = body.SourceColumn,
                        TargetSchema = body.TargetSchema,
                        TargetTable = body.TargetTable,
                        TargetColumn = body.TargetColumn,
                        Label = body.Label,
                        Cardinality = body.Cardinality
                    }, ct))
            .WithName("CreateSchemaRelationship");

        relationships.MapPut("/relationships/{relationshipId:int}",
            async (int relationshipId, UpdateSchemaRelationshipBody body, IMediator m, CancellationToken ct) =>
            {
                await m.Send(new UpdateSchemaRelationshipCommand
                {
                    Id = relationshipId,
                    Label = body.Label,
                    Cardinality = body.Cardinality
                }, ct);
                return TypedResults.NoContent();
            }).WithName("UpdateSchemaRelationship");

        relationships.MapPost("/relationships/{relationshipId:int}/verify",
            async (int relationshipId, VerifySchemaRelationshipBody body, IMediator m, CancellationToken ct) =>
            {
                await m.Send(new VerifySchemaRelationshipCommand(relationshipId, body.IsVerified), ct);
                return TypedResults.NoContent();
            }).WithName("VerifySchemaRelationship");

        relationships.MapDelete("/relationships/{relationshipId:int}",
            async (int relationshipId, IMediator m, CancellationToken ct) =>
            {
                await m.Send(new DeleteSchemaRelationshipCommand(relationshipId), ct);
                return TypedResults.NoContent();
            }).WithName("DeleteSchemaRelationship");

        relationships.MapPost("/relationships/discover-preview",
                (int dataSourceId, IMediator m, CancellationToken ct) =>
                    m.Send(new PreviewSchemaRelationshipDiscoveryQuery(dataSourceId), ct))
            .WithName("PreviewSchemaRelationshipDiscovery");

        relationships.MapGet("/schema-health",
                (int dataSourceId, IMediator m, CancellationToken ct) =>
                    m.Send(new GetSchemaHealthQuery(dataSourceId), ct))
            .WithName("GetSchemaHealth");

        return group;
    }
}

internal record CreateSchemaRelationshipBody(
    string SourceSchema,
    string SourceTable,
    string SourceColumn,
    string TargetSchema,
    string TargetTable,
    string TargetColumn,
    string? Label,
    SchemaRelationshipCardinality Cardinality);

internal record UpdateSchemaRelationshipBody(string? Label, SchemaRelationshipCardinality Cardinality);

internal record VerifySchemaRelationshipBody(bool IsVerified);
