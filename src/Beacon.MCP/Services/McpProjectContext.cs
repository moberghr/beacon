using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Mcp;

namespace Beacon.MCP.Services;

internal interface IProjectContext
{
    List<int>? AllowedProjectIds { get; set; }
    int? ActiveProjectId { get; set; }
    int? UserId { get; set; }
    int? ApiKeyId { get; set; }
}

internal sealed class McpProjectContext : IProjectContext
{
    public List<int>? AllowedProjectIds { get; set; }
    public int? ActiveProjectId { get; set; }
    public int? UserId { get; set; }
    public int? ApiKeyId { get; set; }
}

internal static class ProjectContextFactory
{
    public static IProjectContext Create(IServiceProvider sp)
    {
        var ctx = sp.GetRequiredService<McpProjectContext>();
        var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
        var httpContext = httpContextAccessor.HttpContext;

        if (httpContext?.User != null)
        {
            var user = httpContext.User;

            var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier);
            ctx.UserId = userIdClaim != null && int.TryParse(userIdClaim.Value, out var uid) ? uid : null;

            // Fail CLOSED by default: absent/empty restriction claim denies all projects (empty list),
            // never null. Null would be read downstream as "unrestricted" for explicit project_id
            // requests (see ResolveProjectId), which is a fail-open security bug.
            ctx.AllowedProjectIds = [];

            // The key id and the project restriction are honoured only from the principals that mint them — an API key
            // or a mapped MCP caller. Any other principal (a browser session, a REST bearer token) carrying these claim
            // types got them from elsewhere and is not trusted with them: no key id, and no projects.
            if (!BeaconScopes.IsScopedCaller(user))
            {
                return ctx;
            }

            var apiKeyIdClaim = user.FindFirst(McpCallerClaimTypes.ApiKeyId);
            ctx.ApiKeyId = apiKeyIdClaim != null && int.TryParse(apiKeyIdClaim.Value, out var akid) ? akid : null;

            var allowedProjectsClaim = user.FindFirst(McpCallerClaimTypes.AllowedProjects);
            if (allowedProjectsClaim != null && !string.IsNullOrEmpty(allowedProjectsClaim.Value))
            {
                try
                {
                    // A null deserialization result (e.g. literal "null") also fails closed to an empty list.
                    ctx.AllowedProjectIds = JsonSerializer.Deserialize<List<int>>(allowedProjectsClaim.Value) ?? [];
                }
                catch (JsonException ex)
                {
                    // Fail CLOSED: a malformed restriction claim must deny all projects (empty list), not
                    // fall through to null — downstream ResolveProjectId treats null as "unrestricted"
                    // for explicit project_id requests, which would be a fail-open security bug.
                    ctx.AllowedProjectIds = [];
                    sp.GetService<ILogger<McpProjectContext>>()?
                        .LogWarning(ex, "Failed to parse 'allowed_projects' claim for user {UserId}; denying all project access.", ctx.UserId);
                }
            }
        }

        return ctx;
    }
}
