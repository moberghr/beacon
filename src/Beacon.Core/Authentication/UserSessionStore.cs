using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Authentication;

/// <summary>
/// The user-store side of a <c>Beacon.Auth</c> sign-in session: the stored user a session names, read when the session
/// is signed in and when it is revalidated, and the security stamp that signs out every session of a user when it
/// changes.
/// </summary>
internal interface IUserSessionStore
{
    /// <summary>
    /// The user a session names by its <c>NameIdentifier</c> (<c>Users.ExternalId</c>), archived and disabled users
    /// included (they are refused, not mistaken for missing), with the names of the roles they hold now; null when none.
    /// </summary>
    Task<SessionUser?> FindAsync(string externalId, CancellationToken cancellationToken);

    /// <summary>
    /// Gives user <paramref name="userId"/> a new security stamp: every session they hold is signed out when it is next
    /// revalidated. False when there is no such user.
    /// </summary>
    Task<bool> RenewStampAsync(int userId, CancellationToken cancellationToken);
}

/// <summary>
/// A stored user as a session sees it: its id, whether it is enabled and not archived, its security stamp (null until it
/// first changes) and the names of the roles it holds.
/// </summary>
internal sealed class SessionUser
{
    public int Id { get; init; }

    public bool IsActive { get; init; }

    public string? SecurityStamp { get; init; }

    public IReadOnlyList<string> RoleNames { get; init; } = [];
}

/// <summary>New security stamp values: 128 random bits, as 32 lowercase hex characters.</summary>
internal static class SecurityStamps
{
    public static string New()
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    }
}

internal sealed class UserSessionStore(IDbContextFactory<BeaconContext> contextFactory, ISecurityAuditRecorder securityAudit) : IUserSessionStore
{
    public async Task<SessionUser?> FindAsync(string externalId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Users
            .IgnoreQueryFilters()
            .Where(x => x.ExternalId == externalId)
            .OrderBy(x => x.Id)
            .Select(x =>
                new SessionUser
                {
                    Id = x.Id,
                    IsActive = x.IsEnabled && x.ArchivedTime == null,
                    SecurityStamp = x.SecurityStamp,
                    RoleNames = x.UserRoles
                        .Select(y => y.Role.Name)
                        .ToList()
                })
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> RenewStampAsync(int userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var stamp = SecurityStamps.New();

        // The new stamp and the event commit together; the event names the user, the caller is its actor.
        var changed = await securityAudit.SaveWithEventsAsync(
            context,
            () => context.Users
                .IgnoreQueryFilters()
                .Where(x => x.Id == userId)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.SecurityStamp, stamp), cancellationToken),
            rows => rows > 0
                ? [new SecurityAuditEntry(SecurityAuditActions.SignedOutEverywhere).On(SecurityAuditResources.User, userId)]
                : [],
            cancellationToken);

        return changed > 0;
    }
}
