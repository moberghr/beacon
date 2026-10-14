using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;

namespace Beacon.Api.Authentication;

/// <summary>
/// Notes once at startup what grants functional permissions in this host (stored roles with Beacon user management; on a
/// host without it, the Admin role claim, and the session's other role claims only with
/// <c>Beacon:Authorization:Roles:HonourHostRoleClaims</c>), the role each permission is held through, whether
/// identity-provider app roles are mapped, and whether separation of duties is on. With it on, the number of users whose
/// stored roles already conflict is counted after the host has started, off the startup thread and best effort: a
/// database that cannot be read is noted, never a failure. Configuration values and counts only (§1.11).
/// </summary>
internal sealed class BeaconPermissionStartupNote(
    IOptions<RoleMappingOptions> roleMapping,
    ILogger<BeaconPermissionStartupNote> logger,
    BeaconConfiguration? configuration = null) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        var mapping = roleMapping.Value;
        var userManagement = (configuration ?? new BeaconConfiguration()).UserManagement.Enabled;
        var source = userManagement
            ? "stored roles"
            : mapping.Roles.HonourHostRoleClaims
                ? "the Admin role claim and the session's role claims (HonourHostRoleClaims)"
                : "the Admin role claim only (set Beacon:Authorization:Roles:HonourHostRoleClaims to honour other role claims)";
        logger.LogInformation(
            "Functional permissions are granted by {Source}; roles {Roles}; identity-provider app roles mapped {IdpRolesEnabled} ({IdpRoleCount} entries, honoured {ClaimLifetimeMinutes} minutes after sign-in); separation of duties {SeparationOfDutiesEnabled}",
            source,
            BeaconPermissions.All
                .Select(x => $"{x}={mapping.RoleNameFor(x)}")
                .ToList(),
            mapping.IdpRoles.Enabled,
            mapping.IdpRoles.Map.Count,
            mapping.IdpRoles.ClaimLifetimeMinutes,
            mapping.SeparationOfDuties.Enabled);

        return builder =>
        {
            if (mapping.SeparationOfDuties.Enabled && userManagement)
            {
                var lifetime = builder.ApplicationServices.GetService<IHostApplicationLifetime>();
                lifetime?.ApplicationStarted.Register(
                    () => _ = Task.Run(() => NoteConflictingUsersAsync(builder.ApplicationServices, mapping, lifetime.ApplicationStopping)));
            }

            next(builder);
        };
    }

    private async Task NoteConflictingUsersAsync(IServiceProvider services, RoleMappingOptions mapping, CancellationToken cancellationToken)
    {
        var contextFactory = services.GetService<IDbContextFactory<BeaconContext>>();
        if (contextFactory == null)
        {
            return;
        }

        try
        {
            var count = await SeparationOfDutiesReport.CountUsersWithConflictsAsync(contextFactory, mapping, cancellationToken);
            if (count > 0)
            {
                logger.LogWarning(
                    "Separation of duties is on and {UserCount} users hold both permissions of a conflicting pair through their stored roles: they hold neither permission of the pair until an Admin changes their roles",
                    count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(
                "Separation of duties is on; the users whose stored roles conflict could not be counted ({ExceptionType})",
                ex.GetType().Name);
        }
    }
}
