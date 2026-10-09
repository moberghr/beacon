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

        EnrichBeaconClaims(context, user);
    }

    public static Task HandleRemoteFailureAsync(RemoteFailureContext context)
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OpenIdConnectEvents>>();
        // A subject Beacon refused (not admitted, disabled or archived) gets its own message on the login page; the
        // exception carries no detail, so there is nothing to log beyond the reason already logged above.
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
        // identity's role claim type is), its groups and directory roles, and any beacon:* claim are removed before
        // Beacon's own claims are added.
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

    private static bool IsAuthorizationClaim(string claimType, string roleClaimType)
    {
        return string.Equals(claimType, roleClaimType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(claimType, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase)
            || AuthorizationClaimTypes.Contains(claimType)
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
