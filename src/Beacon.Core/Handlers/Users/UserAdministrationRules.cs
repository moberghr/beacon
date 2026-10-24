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
/// so a UserAdmin cannot grant or keep more than it was given. Nor does a UserAdmin control how anyone signs in (403): it
/// creates no user with a password it chose (only external users, whose sign-in the identity provider controls) and
/// changes no user's user name or e-mail address, so it cannot make an account it can sign in to. Anyone else is refused
/// (403). With separation of duties on (<see cref="SeparationOfDutiesOptions"/>), roles that would give one user both
/// permissions of a conflicting pair are refused (400), whoever assigns them. Every user created and every role
/// assignment is logged with ids only (<see cref="ProjectAccessEvents.UserCreated"/>,
/// <see cref="ProjectAccessEvents.UserRolesChanged"/>).
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

    public const string PasswordUserMessage =
        "Only an Admin can create an internal user, who signs in with a password set here; a UserAdmin can create external users.";

    public const string SignInIdentityMessage = "Only an Admin can change the user name or e-mail address a user signs in with.";

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

    /// <summary>
    /// Refuses creating an internal user, who signs in with the password the caller sets, to a caller who is not an Admin
    /// (403): whoever chose the password could sign in as the new user.
    /// </summary>
    public async Task DemandMayCreateInternalUserAsync(CancellationToken cancellationToken)
    {
        var permissions = await DemandUserAdministrationAsync(null, cancellationToken);
        if (permissions.IsAdmin)
        {
            return;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        throw AccessRefusal.Of(logger, actor, "user", null, PasswordUserMessage);
    }

    /// <summary>
    /// The user name and e-mail address an update of user <paramref name="userId"/> stores, and a refusal (403) when a
    /// caller who is not an Admin changes either. A value sent exactly as stored is kept as it is, surrounding whitespace
    /// included (an account written before the trimming rules, or by an identity provider sign-in), and changes nothing;
    /// any other value is trimmed, and is a change only when it then differs from the stored one. A missing e-mail address
    /// and an empty one are the same.
    /// </summary>
    public async Task<(string UserName, string? Email)> DemandMayChangeSignInIdentityAsync(
        int userId,
        string? userName,
        string? email,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await context.Users
            .Where(x => x.Id == userId)
            .Select(x =>
                new
                {
                    x.UserName,
                    x.Email
                })
            .FirstOrDefaultAsync(cancellationToken);

        // An unknown user is the update's to answer (not found); there is nothing to compare with.
        if (stored == null)
        {
            return (UserAccountFields.Trimmed(userName), UserAccountFields.TrimmedEmail(email));
        }

        var newUserName = userName == stored.UserName ? stored.UserName : UserAccountFields.Trimmed(userName);
        var newEmail = EmptyAsNull(email) == EmptyAsNull(stored.Email) ? stored.Email : UserAccountFields.TrimmedEmail(email);
        if (stored.UserName == newUserName && EmptyAsNull(stored.Email) == EmptyAsNull(newEmail))
        {
            return (newUserName, newEmail);
        }

        var permissions = await permissionAccessor.GetCurrentAsync(cancellationToken);
        if (permissions.IsAdmin)
        {
            return (newUserName, newEmail);
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        throw AccessRefusal.Of(logger, actor, "user", userId, SignInIdentityMessage);
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
    /// Logs a new user (named <paramref name="userName"/>, unique), internal or external, and the roles it was created
    /// with, by the new user's id: the roles it holds, so a grant of the Admin role that waits for approval is not logged as
    /// assigned.
    /// </summary>
    public async Task LogCreatedAsync(
        string userName,
        bool isInternalUser,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var created = await context.Users
            .Where(x => x.UserName == userName)
            .Select(x =>
                new
                {
                    x.Id,
                    RoleIds = x.UserRoles
                        .Select(y => y.RoleId)
                        .ToList()
                })
            .FirstOrDefaultAsync(cancellationToken);
        if (created == null)
        {
            return;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        logger.LogInformation(
            ProjectAccessEvents.UserCreated,
            "User created: user {UserId}, internal {IsInternalUser}, by user {ActorId}",
            created.Id,
            isInternalUser,
            actor.LogId);

        await LogRolesChangedAsync(created.Id, created.RoleIds, [], cancellationToken);
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

        // A grant records who made it (AssignedByUserId); a caller without a user id leaves it empty, which is said here.
        if (addedRoleIds.Count > 0 && actor.UserId == null)
        {
            logger.LogWarning(
                ProjectAccessEvents.UserRolesChanged,
                "Roles {AddedRoleIds} were granted to user {UserId} by a caller without a user id: who granted them is recorded as empty",
                addedRoleIds,
                userId);
        }
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

    private static string? EmptyAsNull(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
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
