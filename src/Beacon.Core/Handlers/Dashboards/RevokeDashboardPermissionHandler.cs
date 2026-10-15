using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Dashboards.RevokeDashboardPermission;

/// <summary>
/// Revokes a user's permission on a dashboard; for the dashboard's owner or an admin of it. The revocation is recorded in
/// the security audit (the user by <c>Users.Id</c>, the level they held) in the same save.
/// </summary>
internal sealed class RevokeDashboardPermissionHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IDashboardService dashboardService,
    IBeaconUserContext userContext,
    ISecurityAuditRecorder securityAudit) : IRequestHandler<RevokeDashboardPermissionCommand, Unit>
{
    public async Task<Unit> Handle(RevokeDashboardPermissionCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var permission = await context.DashboardPermissions
            .Where(x => x.Id == request.PermissionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (permission == null)
        {
            return Unit.Value;
        }

        // Only an owner/admin of the parent dashboard may revoke permissions
        var userId = userContext.UserId ?? string.Empty;
        var hasPermission = await dashboardService.UserHasPermissionAsync(
            permission.DashboardId,
            userId,
            DashboardPermissionLevel.Admin,
            cancellationToken);

        if (!hasPermission)
        {
            throw new InvalidOperationException("Only dashboard owner or admin can revoke permissions");
        }

        context.DashboardPermissions.Remove(permission);
        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.DashboardShareRevoked)
                .On(SecurityAuditResources.Dashboard, permission.DashboardId)
                .With(new SecurityAuditDetails()
                    .Add("permissionId", permission.Id)
                    .Add("level", permission.PermissionLevel))
                .Naming("granteeUserId", permission.UserId),
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}

[ProjectScopeExempt("dashboards are project-less and carry their own access list")]
public record RevokeDashboardPermissionCommand(int PermissionId) : IRequest<Unit>;
