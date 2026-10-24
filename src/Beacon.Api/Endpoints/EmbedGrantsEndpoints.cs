using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Embed;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

/// <summary>
/// Managing embed grants (SecurityAdmin): create one (its secret is returned once), list them, revoke one. Mapped only
/// while embedding is enabled (<c>Beacon:Embed:Enabled</c>).
/// </summary>
internal static class EmbedGrantsEndpoints
{
    public static RouteGroupBuilder MapEmbedGrantsEndpoints(this RouteGroupBuilder group)
    {
        var grants = group.MapGroup("/embed-grants").WithTags("Embed grants");

        grants.MapPost("/", (CreateEmbedGrantBody body, IMediator m, CancellationToken ct) =>
                m.Send(new CreateEmbedGrantCommand
                {
                    ProjectId = body.ProjectId,
                    QueryId = body.QueryId,
                    Name = body.Name,
                    AllowedOrigins = body.AllowedOrigins ?? [],
                    ExpiresInDays = body.ExpiresInDays,
                    FixedParameters = body.FixedParameters,
                    LockedParameters = body.LockedParameters
                }, ct))
            .WithName("CreateEmbedGrant")
            .RequireBeaconPermission(BeaconPermissions.SecurityAdminPolicy);

        grants.MapGet("/", ([AsParameters] ListEmbedGrantsQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("ListEmbedGrants")
            .RequireBeaconPermission(BeaconPermissions.SecurityAdminPolicy);

        grants.MapPost("/{id:int}/revoke", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new RevokeEmbedGrantCommand(id), ct))
            .WithName("RevokeEmbedGrant")
            .RequireBeaconPermission(BeaconPermissions.SecurityAdminPolicy);

        return group;
    }
}

internal sealed record CreateEmbedGrantBody(
    int ProjectId,
    int QueryId,
    string Name,
    IReadOnlyList<string>? AllowedOrigins,
    int ExpiresInDays,
    IReadOnlyDictionary<string, string>? FixedParameters,
    IReadOnlyList<string>? LockedParameters);
