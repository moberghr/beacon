using Beacon.Core.Handlers.McpGlossary;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

/// <summary>
/// REST surface for the admin-managed business glossary (Tier-3 ⑪). One endpoint = one MediatR handler
/// (§2.1.1); endpoints stay thin — resolve path/body, call <c>mediator.Send</c>, return the result.
/// Business definitions are a governance surface, so the whole group is gated with the admin policy
/// (matching MCP/eval management), not the base authenticated policy (§1.1/§1.4).
/// </summary>
internal static class GlossaryEndpoints
{
    public static RouteGroupBuilder MapGlossaryEndpoints(this RouteGroupBuilder group)
    {
        var glossary = group.MapGroup("/glossary").WithTags("Glossary")
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        glossary.MapPost("/", (CreateGlossaryTermBody body, IMediator m, CancellationToken ct) =>
                m.Send(new CreateGlossaryTermCommand
                {
                    ProjectId = body.ProjectId,
                    DataSourceId = body.DataSourceId,
                    Term = body.Term,
                    Synonyms = body.Synonyms,
                    Definition = body.Definition,
                    TargetSchema = body.TargetSchema,
                    TargetTable = body.TargetTable,
                    TargetColumn = body.TargetColumn,
                    MetricExpression = body.MetricExpression
                }, ct))
            .WithName("CreateGlossaryTerm");

        glossary.MapPut("/{id:int}", async (int id, UpdateGlossaryTermBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateGlossaryTermCommand
            {
                Id = id,
                DataSourceId = body.DataSourceId,
                Term = body.Term,
                Synonyms = body.Synonyms,
                Definition = body.Definition,
                TargetSchema = body.TargetSchema,
                TargetTable = body.TargetTable,
                TargetColumn = body.TargetColumn,
                MetricExpression = body.MetricExpression,
                IsActive = body.IsActive
            }, ct);
            return TypedResults.NoContent();
        }).WithName("UpdateGlossaryTerm");

        glossary.MapDelete("/{id:int}", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new DeleteGlossaryTermCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("DeleteGlossaryTerm");

        glossary.MapGet("/", (
                [FromQuery] int projectId,
                [FromQuery] int? dataSourceId,
                [FromQuery] bool includeInactive,
                IMediator m,
                CancellationToken ct) =>
                m.Send(new GetGlossaryTermsQuery
                {
                    ProjectId = projectId,
                    DataSourceId = dataSourceId,
                    IncludeInactive = includeInactive
                }, ct))
            .WithName("GetGlossaryTerms");

        return group;
    }
}

internal sealed record CreateGlossaryTermBody(
    int ProjectId,
    int? DataSourceId,
    string Term,
    string? Synonyms,
    string Definition,
    string? TargetSchema,
    string? TargetTable,
    string? TargetColumn,
    string? MetricExpression);

internal sealed record UpdateGlossaryTermBody(
    int? DataSourceId,
    string? Term,
    string? Synonyms,
    string? Definition,
    string? TargetSchema,
    string? TargetTable,
    string? TargetColumn,
    string? MetricExpression,
    bool? IsActive);
