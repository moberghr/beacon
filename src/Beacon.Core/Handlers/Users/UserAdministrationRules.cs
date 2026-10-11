using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Users;

/// <summary>
/// What user management demands of its caller, decided by the roles the caller holds now. An Admin changes any user and
/// assigns any role. A UserAdmin who is not an Admin manages the users who hold no protected role
/// (<see cref="ProtectedUsers"/>: the Admin role, a functional role): never their own account or roles, never an Admin's,
/// a super admin's or a functional role holder's account (other UserAdmins included), and never a protected role (403);
/// so a UserAdmin cannot grant or keep more than it was given. Anyone else is refused (403). With separation of duties on
/// (<see cref="SeparationOfDutiesOptions"/>), roles that would give one user both permissions of a conflicting pair are
/// refused (400), whoever assigns them. Every role assignment is logged with ids only
/// (<see cref="ProjectAccessEvents.UserRolesChanged"/>).
/// </summary>
internal sealed class UserAdministrationRules(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    IBeaconPermissionAccessor permissionAccessor,
    IOptions<RoleMappingOptions> roleMapping,
    ILogger<UserAdministrationRules> logger)
{
    public const string OwnAccountMessage = "You cannot change your own account or roles; an Admin can.";

    public const string ProtectedUserMessage =
        "Only an Admin can change the account of an Admin, a super admin or a holder of a functional role.";

    public const string ProtectedRoleMessage = "Only an Admin can assign or remove the Admin role or a functional role.";

    /// <summary>Refuses a change to user <paramref name="userId"/>'s account or roles by a caller who may not make it.</summary>
    public async Task DemandMayChangeUserAsync(int userId, CancellationToken cancellationToken)
    {
        var permissions = await DemandUserAdministrationAsync(userId, cancellationToken);
        if (permissions.IsAdmin)
        {
            return;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (actor.BeaconUserId == userId)
        {
            throw AccessRefusal.Of(logger, actor, "user", userId, OwnAccountMessage);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await ProtectedUsers.IsProtectedAsync(context, userId, roleMapping.Value, cancellationToken))
        {
            throw AccessRefusal.Of(logger, actor, "user", userId, ProtectedUserMessage);
        }
    }

    /// <summary>The roles that exist among <paramref name="roleIds"/>, as the rules decide them.</summary>
    public async Task<List<AssignableRole>> RolesAsync(IEnumerable<int> roleIds, CancellationToken cancellationToken)
    {
        var ids = roleIds
            .Distinct()
            .ToList();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Roles
            .Where(x => ids.Contains(x.Id))
            .Select(x =>
                new AssignableRole(x.Id, x.Name, x.IsSystemRole, x.Level))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Refuses roles the caller may not assign (<paramref name="assigned"/>, added or removed: 403) and a set of roles
    /// separation of duties keeps apart (<paramref name="resulting"/>, what the user would hold: 400).
    /// </summary>
    public async Task DemandMayAssignAsync(
        int? userId,
        IReadOnlyCollection<AssignableRole> assigned,
        IReadOnlyCollection<AssignableRole> resulting,
        CancellationToken cancellationToken)
    {
        var permissions = await DemandUserAdministrationAsync(userId, cancellationToken);
        if (!permissions.IsAdmin && assigned.Any(IsProtectedRole))
        {
            var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

            throw AccessRefusal.Of(logger, actor, "user roles", userId, ProtectedRoleMessage);
        }

        DemandSeparated(resulting);
    }

    /// <summary>
    /// Logs the roles a new user was created with (named <paramref name="userName"/>, unique), by the new user's id.
    /// </summary>
    public async Task LogAssignedRolesAsync(string userName, IReadOnlyCollection<AssignableRole> roles, CancellationToken cancellationToken)
    {
        if (roles.Count == 0)
        {
            return;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var userId = await context.Users
            .Where(x => x.UserName == userName)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (userId != null)
        {
            await LogRolesChangedAsync(
                userId.Value,
                roles
                    .Select(x => x.Id)
                    .ToList(),
                [],
                cancellationToken);
        }
    }

    /// <summary>Logs a change to user <paramref name="userId"/>'s roles: role ids only.</summary>
    public async Task LogRolesChangedAsync(
        int userId,
        IReadOnlyCollection<int> addedRoleIds,
        IReadOnlyCollection<int> removedRoleIds,
        CancellationToken cancellationToken)
    {
        if (addedRoleIds.Count == 0 && removedRoleIds.Count == 0)
        {
            return;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        logger.LogInformation(
            ProjectAccessEvents.UserRolesChanged,
            "User roles changed: user {UserId}, added roles {AddedRoleIds}, removed roles {RemovedRoleIds}, by user {ActorId}",
            userId,
            addedRoleIds,
            removedRoleIds,
            actor.LogId);
    }

    // The Admin role and the functional roles are an Admin's to assign.
    private bool IsProtectedRole(AssignableRole role)
    {
        return ProtectedUsers.IsProtectedRole(role.Name, role.IsSystemRole, role.Level, roleMapping.Value);
    }

    // An Admin or a UserAdmin now, whatever role claims the session was signed in with.
    private async Task<BeaconPermissionSet> DemandUserAdministrationAsync(int? userId, CancellationToken cancellationToken)
    {
        var permissions = await permissionAccessor.GetCurrentAsync(cancellationToken);
        if (permissions.Holds(BeaconPermissions.UserAdmin))
        {
            return permissions;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        throw AccessRefusal.Of(logger, actor, "user", userId, "Only an Admin or a UserAdmin can manage users.");
    }

    private void DemandSeparated(IReadOnlyCollection<AssignableRole> roles)
    {
        var options = roleMapping.Value;
        if (!options.SeparationOfDuties.Enabled)
        {
            return;
        }

        var permissions = roles
            .Select(x => options.PermissionOfStoredRole(x.Name, x.IsSystemRole, x.Level))
            .OfType<string>()
            .ToHashSet();
        var conflict = BeaconPermissions.FirstConflict(permissions);
        if (conflict != null)
        {
            throw new InvalidOperationException(
                $"Separation of duties: one user cannot hold both {conflict.Value.First} and {conflict.Value.Second}.");
        }
    }
}

/// <summary>A role as the user administration rules decide it: its name, and whether it is a system role at which level.</summary>
internal sealed record AssignableRole(int Id, string Name, bool IsSystemRole, int Level);
