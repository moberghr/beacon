using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Beacon.Core.Services.Security;

namespace Beacon.SampleProject.Authentication;

public sealed class ApiKeyAuthMiddleware(RequestDelegate next)
{
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

        if (credential == null)
        {
            await Results.Problem(
                title: "Unauthorized",
                detail: "Invalid or expired API key.",
                statusCode: StatusCodes.Status401Unauthorized)
                .ExecuteAsync(context);
            return;
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
            new(ClaimTypes.NameIdentifier, credential.UserId?.ToString() ?? credential.Id.ToString()),
            new("api_key_id", credential.Id.ToString()),
            new("api_key_name", credential.Name),
            new("auth_method", "api_key")
        };

        // Add scope claims
        if (credential.Scopes != null)
        {
            string[]? scopes;
            try
            {
                scopes = JsonSerializer.Deserialize<string[]>(credential.Scopes);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Malformed scopes JSON for API key {ApiKeyId}.", credential.Id);
                await Results.Problem(
                    title: "Unauthorized",
                    detail: "Invalid or expired API key.",
                    statusCode: StatusCodes.Status401Unauthorized)
                    .ExecuteAsync(context);
                return;
            }

            if (scopes != null)
            {
                foreach (var scope in scopes)
                    claims.Add(new Claim("scope", scope));
            }
        }

        // Add project restriction claims
        if (credential.AllowedProjectIds != null)
        {
            claims.Add(new Claim("allowed_projects", credential.AllowedProjectIds));
        }

        // Add user claims if linked to a user
        if (credential.User != null)
        {
            claims.Add(new Claim(ClaimTypes.Name, credential.User.DisplayName ?? credential.User.UserName));
            claims.Add(new Claim("username", credential.User.UserName));
        }

        var identity = new ClaimsIdentity(claims, "ApiKey");
        context.User = new ClaimsPrincipal(identity);

        await next(context);
    }
}
