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

namespace Beacon.SampleProject.Authentication;

internal static class OidcEventHandlers
{
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
                oidcOptions.DefaultRoleName,
                context.HttpContext.RequestAborted);
        }
        catch (BeaconException ex)
        {
            logger.LogWarning("SSO sign-in denied for external id {ExternalId}: {Reason}", externalId, ex.Message);
            context.Fail(ex.Message);
            return;
        }

        EnrichBeaconClaims(context, user);
    }

    public static Task HandleRemoteFailureAsync(RemoteFailureContext context)
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OpenIdConnectEvents>>();
        logger.LogWarning(context.Failure, "OIDC remote failure");

        context.Response.Redirect("/beacon/login?ssoError=1");
        context.HandleResponse();
        return Task.CompletedTask;
    }

    private static void EnrichBeaconClaims(TokenValidatedContext context, BeaconUserData user)
    {
        var identity = context.Principal!.Identities.First();

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

        ReplaceClaim(identity, BeaconClaims.UserId, user.ExternalId);
        ReplaceClaim(identity, BeaconClaims.UserName, user.UserName);

        var existingRoles = identity.FindAll(ClaimTypes.Role).ToList();
        foreach (var claim in existingRoles)
        {
            identity.RemoveClaim(claim);
        }

        foreach (var existingSemRole in identity.FindAll(BeaconClaims.Role).ToList())
        {
            identity.RemoveClaim(existingSemRole);
        }

        foreach (var role in user.Roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role.Name));
            identity.AddClaim(new Claim(BeaconClaims.Role, role.Name));
        }

        context.Properties!.IsPersistent = true;
        context.Properties.AllowRefresh = true;
    }

    private static string? GetClaim(ClaimsPrincipal principal, string type)
    {
        return principal.FindFirst(type)?.Value;
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
