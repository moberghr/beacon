using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Beacon.Core.Mcp;

namespace Beacon.Api.Endpoints;

/// <summary>
/// Confines a project-restricted caller to its projects on a <c>/projects/{id}/...</c> endpoint (§1.4). Applied with
/// <see cref="ProjectAccessEndpointFilterExtensions.RequireProjectAccess{TBuilder}"/>; the project id is read from the
/// <c>id</c> route value.
/// </summary>
internal sealed class ProjectAccessEndpointFilter(string routeValueName) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var raw = context.HttpContext.Request.RouteValues.TryGetValue(routeValueName, out var value) ? value?.ToString() : null;
        if (!int.TryParse(raw, out var projectId) || !ProjectAccess.IsAllowed(context.HttpContext.User, projectId))
        {
            return Results.Problem(
                title: "Forbidden",
                detail: "Your credentials are not authorized for this project.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }
}

/// <summary>
/// The project check shared by REST endpoints, matching how MCP reads the same claims (<c>ProjectContextFactory</c>):
/// <list type="bullet">
/// <item>An <c>allowed_projects</c> claim, whoever carries it, restricts the caller to the listed ids; a malformed value denies.</item>
/// <item>An API key or MCP JWT caller without the claim is denied (fail closed, as MCP and the playground do).</item>
/// <item>Any other principal (cookie / OIDC / JWT user session) carries no project restriction and is allowed.</item>
/// </list>
/// </summary>
internal static class ProjectAccess
{
    public static bool IsAllowed(ClaimsPrincipal user, int projectId)
    {
        var restriction = user.FindFirst(McpCallerClaimTypes.AllowedProjects);
        if (restriction == null)
        {
            var isScopedCaller = user.HasClaim(McpCallerClaimTypes.AuthMethod, "api_key")
                || user.HasClaim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.McpCallerAuthMethod);

            return !isScopedCaller;
        }

        if (string.IsNullOrWhiteSpace(restriction.Value))
        {
            return false;
        }

        try
        {
            var allowed = JsonSerializer.Deserialize<List<int>>(restriction.Value);

            return allowed != null && allowed.Contains(projectId);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal static class ProjectAccessEndpointFilterExtensions
{
    /// <summary>Denies (403) a project-restricted caller whose <c>allowed_projects</c> does not include the route's project.</summary>
    public static TBuilder RequireProjectAccess<TBuilder>(this TBuilder builder, string routeValueName = "id")
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(new ProjectAccessEndpointFilter(routeValueName));

        return builder;
    }
}
