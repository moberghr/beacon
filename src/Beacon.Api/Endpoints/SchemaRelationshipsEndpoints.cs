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
                ([AsParameters] GetSchemaRelationshipsQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
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

        // The answer says whether the relationship is still verified: a non-curator's change to a verified one unverifies it.
        relationships.MapPut("/relationships/{relationshipId:int}",
                (int dataSourceId, int relationshipId, UpdateSchemaRelationshipBody body, IMediator m, CancellationToken ct) =>
                    m.Send(new UpdateSchemaRelationshipCommand
                    {
                        DataSourceId = dataSourceId,
                        Id = relationshipId,
                        Label = body.Label,
                        Cardinality = body.Cardinality
                    }, ct))
            .WithName("UpdateSchemaRelationship");

        // Verifying names the label and cardinality the curator reviewed: a relationship holding other content is not verified (409).
        relationships.MapPost("/relationships/{relationshipId:int}/verify",
            async (int dataSourceId, int relationshipId, VerifySchemaRelationshipBody body, IMediator m, CancellationToken ct) =>
            {
                await m.Send(
                    new VerifySchemaRelationshipCommand(dataSourceId, relationshipId, body.IsVerified)
                    {
                        ReviewedLabel = body.ReviewedLabel,
                        ReviewedCardinality = body.ReviewedCardinality
                    },
                    ct);
                return TypedResults.NoContent();
            }).WithName("VerifySchemaRelationship");

        // A verified relationship needs a curator of the data source (403 otherwise).
        relationships.MapDelete("/relationships/{relationshipId:int}",
            async (int dataSourceId, int relationshipId, IMediator m, CancellationToken ct) =>
            {
                await m.Send(new DeleteSchemaRelationshipCommand(dataSourceId, relationshipId), ct);
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

/// <param name="IsVerified">Verify (true) or unverify (false).</param>
/// <param name="ReviewedLabel">The label the curator reviewed; required to verify.</param>
/// <param name="ReviewedCardinality">The cardinality the curator reviewed; required to verify.</param>
internal record VerifySchemaRelationshipBody(bool IsVerified, string? ReviewedLabel, SchemaRelationshipCardinality? ReviewedCardinality);
