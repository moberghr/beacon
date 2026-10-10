using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Beacon.Core.Authorization;
using Beacon.Core.Mcp;
using Beacon.Core.Services.Security;

namespace Beacon.Api.Authentication;

public sealed class ApiKeyAuthMiddleware(RequestDelegate next)
{
    private static readonly ConcurrentDictionary<int, byte> ExecuteWithheldWarned = new();

    public async Task InvokeAsync(HttpContext context, IApiKeyService apiKeyService, ILogger<ApiKeyAuthMiddleware> logger)
    {
        // Skip if already authenticated
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            await next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader == null || !authHeader.StartsWith("Bearer sk-sem_", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var apiKey = authHeader["Bearer ".Length..].Trim();
        var credential = await apiKeyService.ValidateApiKeyAsync(apiKey, context.RequestAborted);

        // The key store already refuses a key without an active owner; a key without an owner is refused here too, so a
        // replaced IApiKeyService can never make the key id stand in for a user id below.
        if (credential is not { UserId: not null, User: not null })
        {
            await RefuseAsync(context);
            return;
        }

        // Scopes: only Read and Execute. A key stored with the retired Admin scope gets Execute, which is all Admin ever
        // granted; anything else stored is dropped. A malformed value, or one that leaves no valid scope (none stored,
        // or only unknown values), rejects the key: a key that grants nothing is not a credential.
        var storedScopes = ApiKeyGrants.ReadScopes(credential.Scopes);
        if (storedScopes is not { Length: > 0 })
        {
            logger.LogWarning(
                "API key {ApiKeyId} refused: {Reason}",
                credential.Id,
                storedScopes == null ? "malformed scopes" : "no valid scopes");
            await RefuseAsync(context);
            return;
        }

        // The Execute scope follows the owner's current write permission, by the rule it was issued under: a key whose
        // owner lost the Editor role (or super admin) acts as a Read key until the owner regains it.
        var owner = credential.User;
        var ownerCanWrite = ApiKeyGrants.OwnerCanWrite(
            owner.IsSuperAdmin,
            owner.UserRoles.Select(x => x.Role?.Level ?? 0));
        var scopes = ApiKeyGrants.ForOwner(storedScopes, ownerCanWrite);
        if (!ownerCanWrite && storedScopes.Contains(BeaconScopes.Execute))
        {
            WarnOnceWhenExecuteIsWithheld(logger, credential.Id);
        }

        // Update last-used timestamp. Non-critical bookkeeping: a failure here must never block
        // authentication, and it must not run as fire-and-forget on the request-scoped service
        // (the scope — and its DbContext — can dispose before the write completes).
        try
        {
            await apiKeyService.UpdateLastUsedAsync(credential.Id, context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update last-used timestamp for API key {ApiKeyId}.", credential.Id);
        }

        // Build claims identity from API key
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, credential.UserId.Value.ToString()),
            new(McpCallerClaimTypes.ApiKeyId, credential.Id.ToString()),
            new(McpCallerClaimTypes.ApiKeyName, credential.Name),
            new(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod)
        };

        foreach (var scope in scopes)
        {
            claims.Add(new Claim(McpCallerClaimTypes.Scope, scope));
        }

        // Add project restriction claims
        if (credential.AllowedProjectIds != null)
        {
            claims.Add(new Claim(McpCallerClaimTypes.AllowedProjects, credential.AllowedProjectIds));
        }

        // Add the owner's claims
        claims.Add(new Claim(ClaimTypes.Name, credential.User.DisplayName ?? credential.User.UserName));
        claims.Add(new Claim(McpCallerClaimTypes.UserNameClaim, credential.User.UserName));

        var identity = new ClaimsIdentity(claims, McpCallerClaimTypes.ApiKeyAuthenticationType);
        context.User = new ClaimsPrincipal(identity);

        await next(context);
    }

    private static Task RefuseAsync(HttpContext context)
    {
        return Results.Problem(
                title: "Unauthorized",
                detail: "Invalid or expired API key.",
                statusCode: StatusCodes.Status401Unauthorized)
            .ExecuteAsync(context);
    }

    // Once per key per process: the key is used on every request, and the owner's permission rarely changes.
    private static void WarnOnceWhenExecuteIsWithheld(ILogger logger, int apiKeyId)
    {
        if (ExecuteWithheldWarned.TryAdd(apiKeyId, 0))
        {
            logger.LogWarning(
                "API key {ApiKeyId} carries the Execute scope, but its owner has no write permission: it acts as a Read key.",
                apiKeyId);
        }
    }
}
