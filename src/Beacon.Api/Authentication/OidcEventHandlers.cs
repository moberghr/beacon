using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;

namespace Beacon.Api.Authentication;

internal static class OidcEventHandlers
{
    private const string BeaconClaimPrefix = "beacon:";

    private static readonly HashSet<string> AuthorizationClaimTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "role",
        "roles",
        "groups",
        "wids"
    };

    public static async Task HandleTokenValidatedAsync(TokenValidatedContext context)
    {
        var services = context.HttpContext.RequestServices;
        var logger = services.GetRequiredService<ILogger<OpenIdConnectEvents>>();
        var oidcOptions = services.GetRequiredService<IOptions<OidcAuthenticationOptions>>().Value;
        var userService = services.GetRequiredService<IUserManagementService>();

        var principal = context.Principal;
        if (principal == null)
        {
            context.Fail("OIDC sign-in was missing a ClaimsPrincipal.");
            return;
        }

        var externalId = GetClaim(principal, "sub");
        if (string.IsNullOrWhiteSpace(externalId))
        {
            context.Fail("OIDC token did not contain a 'sub' claim.");
            return;
        }

        // Admission runs before anything is looked up or provisioned: a subject outside the allowed tenants, a guest,
        // or a user without a required group/app role never gets a Beacon user.
        var admission = OidcAdmission.Evaluate(principal, oidcOptions);
        if (admission != OidcAdmissionDecision.Admitted)
        {
            LogRefusal(logger, admission.ToString(), principal, externalId);
            context.Fail(new OidcNotAdmittedException());
            return;
        }

        OidcAdmission.WarnOnceWhenGuestSignalMissing(principal, oidcOptions, logger);

        // With RequireMfa, a sign-in the identity provider does not report as multi-factor is refused before anything is
        // looked up or provisioned.
        if (!OidcAdmission.MeetsMfaRequirement(principal, oidcOptions))
        {
            LogRefusal(logger, "MfaNotPerformed", principal, externalId);
            context.Fail(new OidcMfaRequiredException());
            return;
        }

        var identityProvider = GetClaim(principal, "iss") ?? oidcOptions.Authority ?? string.Empty;
        var email = GetClaim(principal, "email");
        var displayName = GetClaim(principal, "name");
        var userName = GetClaim(principal, "preferred_username")
            ?? email
            ?? displayName
            ?? externalId;

        BeaconUserData user;
        try
        {
            user = await userService.GetOrCreateExternalUserAsync(
                externalId,
                identityProvider,
                userName,
                email,
                displayName,
                string.IsNullOrWhiteSpace(oidcOptions.DefaultRoleName) ? null : oidcOptions.DefaultRoleName,
                context.HttpContext.RequestAborted);
        }
        catch (BeaconException ex)
        {
            // Disabled, archived, or first-run setup not completed: the message is a fixed string from the user store.
            LogRefusal(logger, ex.Message, principal, externalId);
            context.Fail(new OidcNotAdmittedException());
            return;
        }

        // Read before the identity provider's role claims are removed below.
        var roleMapping = services.GetService<IOptions<RoleMappingOptions>>()?.Value ?? new RoleMappingOptions();
        var idpPermissions = SeparatedFromStoredRoles(MappedIdpPermissions(principal, roleMapping, user, logger), user, roleMapping, logger);

        EnrichBeaconClaims(context, user);
        AddIdpPermissions(context, roleMapping, idpPermissions, services.GetService<TimeProvider>() ?? TimeProvider.System);
        if (idpPermissions.Count > 0)
        {
            logger.LogInformation(
                "SSO sign-in mapped identity-provider app roles to permissions {Permissions} for user {UserId}",
                idpPermissions,
                user.Id);
        }
    }

    public static Task HandleRemoteFailureAsync(RemoteFailureContext context)
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OpenIdConnectEvents>>();
        // A sign-in refused for want of multi-factor authentication, and a subject Beacon refused (not admitted, disabled
        // or archived), each get their own message on the login page; the exceptions carry no detail, so there is nothing
        // to log beyond the reason already logged above.
        if (context.Failure is OidcMfaRequiredException)
        {
            context.Response.Redirect("/beacon/login?ssoError=mfa_required");
            context.HandleResponse();
            return Task.CompletedTask;
        }

        if (context.Failure is OidcNotAdmittedException)
        {
            context.Response.Redirect("/beacon/login?ssoError=not_admitted");
            context.HandleResponse();
            return Task.CompletedTask;
        }

        logger.LogWarning(context.Failure, "OIDC remote failure");

        context.Response.Redirect("/beacon/login?ssoError=1");
        context.HandleResponse();
        return Task.CompletedTask;
    }

    private static void EnrichBeaconClaims(TokenValidatedContext context, BeaconUserData user)
    {
        var identity = context.Principal!.Identities.First();

        // Authorization comes from Beacon only: every role-like claim the identity provider sent (whatever the
        // identity's role claim type is), its groups and directory roles, any beacon:* claim, and every claim type that
        // marks a scoped caller (auth_method, scope, allowed_projects, api_key_*, caller_*) are removed before Beacon's
        // own claims are added. A browser session never carries a scope, a project restriction or a key id.
        var tokenSupplied = identity.Claims
            .Where(x => IsAuthorizationClaim(x.Type, identity.RoleClaimType))
            .ToList();
        foreach (var claim in tokenSupplied)
        {
            identity.RemoveClaim(claim);
        }

        ReplaceClaim(identity, ClaimTypes.NameIdentifier, user.ExternalId);
        ReplaceClaim(identity, ClaimTypes.Name, user.DisplayName ?? user.UserName);

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            ReplaceClaim(identity, ClaimTypes.Email, user.Email!);
        }

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            ReplaceClaim(identity, "DisplayName", user.DisplayName!);
        }

        identity.AddClaim(new Claim(BeaconClaims.UserId, user.ExternalId));
        identity.AddClaim(new Claim(BeaconClaims.UserName, user.UserName));

        foreach (var role in user.Roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role.Name));
            identity.AddClaim(new Claim(BeaconClaims.Role, role.Name));
            if (!string.Equals(identity.RoleClaimType, ClaimTypes.Role, StringComparison.Ordinal))
            {
                identity.AddClaim(new Claim(identity.RoleClaimType, role.Name));
            }
        }

        context.Properties!.IsPersistent = true;
        context.Properties.AllowRefresh = true;
    }

    // The functional permissions the identity provider's app roles map to (Beacon:Authorization:IdpRoles), when the
    // mapping is on: the values of the roles claim (and the identity's role claim type). When none maps, the counts are
    // logged (role claims seen, app roles mapped) so a misnamed app role can be found; never the values.
    private static IReadOnlyList<string> MappedIdpPermissions(
        ClaimsPrincipal principal,
        RoleMappingOptions roleMapping,
        BeaconUserData user,
        ILogger logger)
    {
        if (!roleMapping.IdpRoles.Enabled)
        {
            return [];
        }

        var roleClaimTypes = principal.Identities
            .Select(x => x.RoleClaimType)
            .Append("roles")
            .Append("role")
            .ToHashSet(StringComparer.Ordinal);
        var roleClaims = principal.Claims
            .Where(x => roleClaimTypes.Contains(x.Type))
            .ToList();
        var permissions = roleClaims
            .Select(x => roleMapping.PermissionOfIdpRole(x.Value))
            .OfType<string>()
            .Distinct()
            .Order()
            .ToList();
        if (permissions.Count == 0)
        {
            logger.LogInformation(
                "SSO sign-in mapped no identity-provider app role to a permission for user {UserId}: {RoleClaimCount} role claims seen, {MappedRoleCount} app roles mapped",
                user.Id,
                roleClaims.Count,
                roleMapping.IdpRoles.Map.Count);
        }

        return permissions;
    }

    // With separation of duties on, a mapped permission that would give the user both permissions of a conflicting pair
    // with their stored functional roles is not added (the accessor would grant neither). Ids and permission names only.
    private static IReadOnlyList<string> SeparatedFromStoredRoles(
        IReadOnlyList<string> mapped,
        BeaconUserData user,
        RoleMappingOptions roleMapping,
        ILogger logger)
    {
        if (!roleMapping.SeparationOfDuties.Enabled || mapped.Count == 0)
        {
            return mapped;
        }

        var held = user.Roles
            .Select(x => roleMapping.PermissionOfStoredRole(x.Name, x.IsSystemRole, x.Level))
            .OfType<string>()
            .ToHashSet();
        var kept = new List<string>();
        foreach (var permission in mapped)
        {
            var conflict = BeaconPermissions.FirstConflict(held.Append(permission).ToHashSet());
            if (conflict != null && (conflict.Value.First == permission || conflict.Value.Second == permission))
            {
                logger.LogWarning(
                    "Separation of duties: the identity-provider permission {Permission} is not added for user {UserId}; it conflicts with {OtherPermission}",
                    permission,
                    user.Id,
                    conflict.Value.First == permission ? conflict.Value.Second : conflict.Value.First);
                continue;
            }

            held.Add(permission);
            kept.Add(permission);
        }

        return kept;
    }

    // For this session only, never stored: the role claim the permission's policy reads, and the claims the permission
    // accessor honours while the mapping is on, issued by Beacon's sign-in and stamped with its time so they count only for
    // IdpRoles:ClaimLifetimeMinutes. An API key never carries either.
    private static void AddIdpPermissions(
        TokenValidatedContext context,
        RoleMappingOptions roleMapping,
        IReadOnlyList<string> permissions,
        TimeProvider timeProvider)
    {
        if (permissions.Count == 0)
        {
            return;
        }

        var identity = context.Principal!.Identities.First();
        identity.AddClaim(new Claim(
            BeaconClaims.IdpPermissionsIssuedAt,
            timeProvider.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ClaimValueTypes.Integer64,
            BeaconClaims.Issuer));
        foreach (var permission in permissions)
        {
            var roleName = roleMapping.RoleNameFor(permission);
            if (!identity.HasClaim(ClaimTypes.Role, roleName))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, roleName));
                identity.AddClaim(new Claim(BeaconClaims.Role, roleName));
                if (!string.Equals(identity.RoleClaimType, ClaimTypes.Role, StringComparison.Ordinal))
                {
                    identity.AddClaim(new Claim(identity.RoleClaimType, roleName));
                }
            }

            identity.AddClaim(new Claim(BeaconClaims.IdpPermission, permission, ClaimValueTypes.String, BeaconClaims.Issuer));
        }
    }

    private static bool IsAuthorizationClaim(string claimType, string roleClaimType)
    {
        return string.Equals(claimType, roleClaimType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(claimType, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase)
            || AuthorizationClaimTypes.Contains(claimType)
            || McpCallerClaimTypes.Reserved.Contains(claimType)
            || claimType.StartsWith(BeaconClaimPrefix, StringComparison.OrdinalIgnoreCase);
    }

    // Reason, tenant, issuer and a subject hash: never the raw subject, an e-mail or other claim values.
    private static void LogRefusal(ILogger logger, string reason, ClaimsPrincipal principal, string externalId)
    {
        logger.LogWarning(
            "SSO sign-in refused: {Reason} (tenant {TenantId}, issuer {Issuer}, subject {SubjectHash}).",
            reason,
            GetClaim(principal, "tid") ?? "-",
            GetClaim(principal, "iss") ?? "-",
            SubjectFingerprint.Of(externalId));
    }

    // Exact (case-sensitive) claim names, as the identity provider sent them (MapInboundClaims is off).
    private static string? GetClaim(ClaimsPrincipal principal, string type)
    {
        var value = principal.Claims
            .Where(x => string.Equals(x.Type, type, StringComparison.Ordinal))
            .Select(x => x.Value)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void ReplaceClaim(ClaimsIdentity identity, string type, string value)
    {
        foreach (var existing in identity.FindAll(type).ToList())
        {
            identity.RemoveClaim(existing);
        }

        identity.AddClaim(new Claim(type, value));
    }
}

/// <summary>
/// The SSO subject authenticated at the identity provider but may not use Beacon (not admitted, disabled or archived).
/// Carries no detail: the reason is logged where the decision is made.
/// </summary>
internal sealed class OidcNotAdmittedException() : Exception("The signed-in account is not permitted to use Beacon.");

/// <summary>
/// The SSO sign-in did not show multi-factor authentication while <see cref="OidcAuthenticationOptions.RequireMfa"/> is on.
/// Carries no detail: the refusal is logged where the decision is made.
/// </summary>
internal sealed class OidcMfaRequiredException() : Exception("The sign-in did not use multi-factor authentication.");
