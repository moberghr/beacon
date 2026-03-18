using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Semantico.Core.Services.Security;

namespace Semantico.UI.Authentication;

public sealed class ApiKeyAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IApiKeyService apiKeyService)
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
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid or expired API key");
            return;
        }

        // Update last used (fire and forget)
        _ = apiKeyService.UpdateLastUsedAsync(credential.Id);

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
            var scopes = JsonSerializer.Deserialize<string[]>(credential.Scopes);
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
