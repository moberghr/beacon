using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Semantico.MCP.Services;

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

internal sealed class ProjectSessionState
{
    public int? ActiveProjectId { get; set; }
}

internal sealed class McpProjectContextManager
{
    private readonly ConcurrentDictionary<string, ProjectSessionState> _sessions = new();

    public ProjectSessionState GetOrCreate(string key)
        => _sessions.GetOrAdd(key, _ => new());

    public void Remove(string key)
        => _sessions.TryRemove(key, out _);

    public static string MakeKey(int? userId, int? apiKeyId)
        => $"u{userId}-k{apiKeyId}";
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

            var apiKeyIdClaim = user.FindFirst("api_key_id");
            ctx.ApiKeyId = apiKeyIdClaim != null && int.TryParse(apiKeyIdClaim.Value, out var akid) ? akid : null;

            var allowedProjectsClaim = user.FindFirst("allowed_projects");
            if (allowedProjectsClaim != null && !string.IsNullOrEmpty(allowedProjectsClaim.Value))
            {
                try { ctx.AllowedProjectIds = JsonSerializer.Deserialize<List<int>>(allowedProjectsClaim.Value); }
                catch { /* ignore malformed claims */ }
            }

            // Restore active project from session manager
            var sessionManager = sp.GetRequiredService<McpProjectContextManager>();
            var key = McpProjectContextManager.MakeKey(ctx.UserId, ctx.ApiKeyId);
            var state = sessionManager.GetOrCreate(key);
            ctx.ActiveProjectId = state.ActiveProjectId;
        }

        return ctx;
    }
}
