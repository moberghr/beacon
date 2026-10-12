using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;

namespace Beacon.Core.Handlers.Users;

internal sealed class SetUserRolesHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    UserAdministrationRules rules,
    IBeaconActorAccessor actorAccessor,
    TimeProvider timeProvider)
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

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
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
                UserId = request.UserId,
                RoleId = roleId,
                AssignedByUserId = actor.UserId,
                AssignedAt = now
            });
        }

        await context.SaveChangesAsync(cancellationToken);

        await rules.LogRolesChangedAsync(request.UserId, added, removed, cancellationToken);
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
/// only, and signs out every session the user holds (a new security stamp). A user that does not exist answers 404, a role
/// that does not exist 400.
/// </summary>
[ProjectScopeExempt("user management, gated by the UserAdmin permission; no project data", Policy = BeaconPermissions.UserAdminPolicy)]
public record SetUserRolesCommand(int UserId, int[] RoleIds) : IRequest;
