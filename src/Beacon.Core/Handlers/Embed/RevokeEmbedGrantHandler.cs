using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.SecurityAudit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.Embed;

/// <summary>
/// Revokes an embed grant: no token is issued for it again, and every token already issued stops rendering, since each
/// exchange and render reads the grant. Any SecurityAdmin (or Admin) may revoke any grant, whatever projects it reads
/// (incident response). Idempotent: revoking a revoked grant changes nothing and records nothing. The revocation is one
/// conditional update, so two revokes at once record one event.
/// </summary>
internal sealed class RevokeEmbedGrantHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    ISecurityAuditRecorder securityAudit,
    TimeProvider timeProvider,
    ILogger<RevokeEmbedGrantHandler> logger)
    : IRequestHandler<RevokeEmbedGrantCommand, RevokeEmbedGrantResult>
{
    public async Task<RevokeEmbedGrantResult> Handle(RevokeEmbedGrantCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var grant = await context.EmbedGrants
            .Where(x => x.Id == request.Id)
            .Select(x =>
                new
                {
                    x.ProjectId,
                    x.RevokedTime
                })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Embed grant #{request.Id} not found.");

        if (grant.RevokedTime != null)
        {
            return new RevokeEmbedGrantResult(request.Id, grant.RevokedTime.Value, Revoked: false);
        }

        var caller = await actorAccessor.GetCurrentAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var revoked = await securityAudit.SaveWithEventsAsync(
            context,
            () => context.EmbedGrants
                .Where(x => x.Id == request.Id)
                .Where(x => x.RevokedTime == null)
                .ExecuteUpdateAsync(
                    x => x
                        .SetProperty(y => y.RevokedTime, now)
                        .SetProperty(y => y.RevokedByUserId, caller.UserId)
                        .SetProperty(y => y.ModifiedByUserId, caller.UserId)
                        .SetProperty(y => y.ModifiedTime, now),
                    cancellationToken),
            rows => rows == 0
                ? []
                : [
                    new SecurityAuditEntry(SecurityAuditActions.EmbedGrantRevoked)
                        .On(SecurityAuditResources.EmbedGrant, request.Id)
                        .InProject(grant.ProjectId)
                ],
            cancellationToken);

        if (revoked == 0)
        {
            // A concurrent revoke won: report the time it recorded.
            var revokedTime = await context.EmbedGrants
                .Where(x => x.Id == request.Id)
                .Select(x => x.RevokedTime)
                .FirstOrDefaultAsync(cancellationToken);

            return new RevokeEmbedGrantResult(request.Id, revokedTime ?? now, Revoked: false);
        }

        logger.LogInformation("Embed grant {GrantId} revoked by user {UserId}", request.Id, caller.LogId);

        return new RevokeEmbedGrantResult(request.Id, now, Revoked: true);
    }
}

[ProjectScopeExempt("incident response: a SecurityAdmin revokes any grant", Policy = BeaconPermissions.SecurityAdminPolicy)]
public sealed record RevokeEmbedGrantCommand(int Id) : IRequest<RevokeEmbedGrantResult>;

/// <param name="Revoked">True when this call revoked the grant; false when it was revoked already.</param>
public sealed record RevokeEmbedGrantResult(int Id, DateTime RevokedTime, bool Revoked);
