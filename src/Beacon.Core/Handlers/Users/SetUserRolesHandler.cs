using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.Users;

internal sealed class SetUserRolesHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    UserAdministrationRules rules,
    IBeaconActorAccessor actorAccessor,
    TimeProvider timeProvider,
    ISecurityAuditRecorder securityAudit,
    AdminRoleGrantApproval adminRoleGrants)
    : IRequestHandler<SetUserRolesCommand>
{
    public async Task Handle(SetUserRolesCommand request, CancellationToken cancellationToken)
    {
        await rules.DemandMayChangeUserAsync(request.UserId, cancellationToken);

        var roleIds = (request.RoleIds ?? [])
            .Distinct()
            .ToList();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var currentRoles = await context.UserRoles
            .Where(x => x.UserId == request.UserId)
            .ToListAsync(cancellationToken);
        var user = await context.Users
            .Where(x => x.Id == request.UserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (user == null)
        {
            throw new BeaconNotFoundException($"User {request.UserId} not found.");
        }

        var currentRoleIds = currentRoles
            .Select(x => x.RoleId)
            .ToList();
        var namedRoleIds = roleIds
            .Union(currentRoleIds)
            .ToList();
        var roles = await rules.RolesAsync(namedRoleIds, cancellationToken);
        var unknown = roleIds
            .Where(x => roles.All(y => y.Id != x))
            .ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException($"Role {unknown[0]} does not exist.");
        }

        var added = roleIds
            .Except(currentRoleIds)
            .Order()
            .ToList();
        var removed = currentRoleIds
            .Except(roleIds)
            .Order()
            .ToList();
        if (added.Count == 0 && removed.Count == 0)
        {
            return;
        }

        await rules.DemandMayAssignAsync(
            request.UserId,
            Among(roles, added.Concat(removed)),
            Among(roles, roleIds),
            cancellationToken);

        // While the workflow reviews grants of the Admin role, adding it submits the grant; the rest of the save applies.
        var adminGrant = adminRoleGrants.Covers
            ? Among(roles, added).FirstOrDefault(x => AdminRoleGrantApproval.IsAdminRole(x.Name))
            : null;
        var applied = added
            .Where(x => x != adminGrant?.Id)
            .ToList();
        var requester = adminGrant == null ? null : await adminRoleGrants.RequesterAsync(request.UserId, cancellationToken);

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (requester == null)
        {
            await StageRoleChangesAsync(context, user, currentRoles, applied, removed, actor.UserId, now, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            var submission = await SaveWithGrantAsync(context, request.UserId, applied, removed, requester, adminGrant!.Id, actor.UserId, now, cancellationToken);
            adminRoleGrants.LogSubmitted(submission, request.UserId);
        }

        await rules.LogRolesChangedAsync(request.UserId, applied, removed, cancellationToken);
    }

    // The Admin role's grant submitted with the rest of the save in one transaction that takes the user's row first, as a
    // decision on the grant takes it. Through the execution strategy; a retry starts from a clean tracker and reads again.
    private async Task<ChangeSubmission> SaveWithGrantAsync(
        BeaconContext context,
        int userId,
        List<int> applied,
        List<int> removed,
        ChangeRequester requester,
        int adminRoleId,
        string? assignedByUserId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            if (await AdminRoleGrantApproval.LockUserAsync(context, userId, cancellationToken) == 0)
            {
                throw new BeaconNotFoundException($"User {userId} not found.");
            }

            var user = await context.Users
                .Where(x => x.Id == userId)
                .FirstAsync(cancellationToken);
            var currentRoles = await context.UserRoles
                .Where(x => x.UserId == userId)
                .ToListAsync(cancellationToken);
            await StageRoleChangesAsync(context, user, currentRoles, applied, removed, assignedByUserId, now, cancellationToken);

            var submission = await adminRoleGrants.SubmitAsync(context, requester, userId, adminRoleId, cancellationToken);
            await securityAudit.SaveWithEventsAsync(context, () => ChangeApprovalAuditEntries.Submitted(submission), cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return submission;
        });
    }

    // The roles removed and added, a new security stamp and the role change's event, staged; nothing when neither changes.
    private async Task StageRoleChangesAsync(
        BeaconContext context,
        BeaconUser user,
        List<BeaconUserRole> currentRoles,
        List<int> added,
        List<int> removed,
        string? assignedByUserId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (added.Count == 0 && removed.Count == 0)
        {
            return;
        }

        foreach (var row in currentRoles.Where(x => removed.Contains(x.RoleId)))
        {
            context.UserRoles.Remove(row);
        }

        // A role change signs out every session the user holds, in the same save.
        user.SecurityStamp = SecurityStamps.New();
        foreach (var roleId in added)
        {
            context.UserRoles.Add(new BeaconUserRole
            {
                UserId = user.Id,
                RoleId = roleId,
                AssignedByUserId = assignedByUserId,
                AssignedAt = now
            });
        }

        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.RolesChanged)
                .On(SecurityAuditResources.User, user.Id)
                .With(new SecurityAuditDetails()
                    .Add("addedRoleIds", added)
                    .Add("removedRoleIds", removed)),
            cancellationToken);
    }

    private static List<AssignableRole> Among(List<AssignableRole> roles, IEnumerable<int> ids)
    {
        return roles
            .Where(x => ids.Contains(x.Id))
            .ToList();
    }
}

/// <summary>
/// Sets the roles a user holds to <c>RoleIds</c>. For an Admin, or a UserAdmin on another user who is no super admin and
/// holds neither the Admin role nor a functional role (another UserAdmin included), and only to roles other than those
/// (403 otherwise). With separation
/// of duties on, roles granting both permissions of a conflicting pair are refused (400). The change is logged with ids
/// only, and signs out every session the user holds (a new security stamp). While the approval workflow reviews grants of
/// the Admin role (<see cref="ApprovalWorkflowOptions.CoversAdminRoleGrants"/>), adding the Admin role does not apply it:
/// the other roles change, and the grant waits for another live Admin's approval (a caller Beacon cannot name: 403).
/// Removing the Admin role, and functional roles, apply at once. A user that does not exist answers 404, a role that does
/// not exist 400.
/// </summary>
[ProjectScopeExempt("user management, gated by the UserAdmin permission; no project data", Policy = BeaconPermissions.UserAdminPolicy)]
public record SetUserRolesCommand(int UserId, int[] RoleIds) : IRequest;
