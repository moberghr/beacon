using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.SecurityAudit;
using MediatR;

namespace Beacon.Api.Endpoints;

internal static class SecurityAuditEndpoints
{
    public static RouteGroupBuilder MapSecurityAuditEndpoints(this RouteGroupBuilder group)
    {
        // The security audit trail is the Auditor's: a paged read over at most 93 days, and a verification of the events'
        // integrity hashes. Both are themselves recorded in the trail.
        var audit = group.MapGroup("/security-audit").WithTags("SecurityAudit");

        audit.MapGet("/", ([AsParameters] GetSecurityAuditEventsQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetSecurityAuditEvents")
            .RequireBeaconPermission(BeaconPermissions.AuditorPolicy);

        audit.MapPost("/verify", (VerifySecurityAuditCommand command, IMediator m, CancellationToken ct) => m.Send(command, ct))
            .WithName("VerifySecurityAudit")
            .RequireBeaconPermission(BeaconPermissions.AuditorPolicy);

        return group;
    }
}
