using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Mcp;

namespace Beacon.MCP.Services;

internal interface IProjectContext
{
    /// <summary>The projects the caller may read through MCP. Null or empty reaches no project (fail closed).</summary>
    List<int>? AllowedProjectIds { get; set; }

    /// <summary>
    /// The readable projects the caller may also work ad hoc in (<see cref="ProjectAction.Query"/>): the SQL-carrying
    /// tools and feedback run only in these. Null or empty: none.
    /// </summary>
    List<int>? QueryProjectIds { get; set; }

    int? ActiveProjectId { get; set; }

    /// <summary>The caller's Beacon user (<c>Users.Id</c>), recorded on its signals and audit rows; null without one.</summary>
    int? UserId { get; set; }

    int? ApiKeyId { get; set; }

    /// <summary>
    /// The caller's project access could not be determined (the evaluator or the database failed): the context reaches no
    /// project, and callers are told to retry rather than that they have no projects.
    /// </summary>
    bool AccessUndetermined { get; set; }
}

internal sealed class McpProjectContext : IProjectContext
{
    public List<int>? AllowedProjectIds { get; set; }
    public List<int>? QueryProjectIds { get; set; }
    public int? ActiveProjectId { get; set; }
    public int? UserId { get; set; }
    public int? ApiKeyId { get; set; }
    public bool AccessUndetermined { get; set; }
}

/// <summary>
/// The MCP request's <see cref="IProjectContext"/>. The context reaches no project until <see cref="PopulateAsync"/>
/// fills it from the caller's project access, which the MCP request filters do before any tool or tool list runs.
/// </summary>
internal static class ProjectContextFactory
{
    /// <summary>What a tool call answers, and a tool list fails with, when the caller's project access could not be determined.</summary>
    public const string AccessUndeterminedMessage = "Project access could not be determined; retry the call.";

    /// <summary>The scope's context, as the tools and services receive it.</summary>
    public static IProjectContext Create(IServiceProvider sp)
    {
        return sp.GetRequiredService<McpProjectContext>();
    }

    /// <summary>
    /// Fills the scope's context for the current MCP request. The caller is recorded by its Beacon user. Projects come
    /// from <see cref="IProjectAccessEvaluator"/>, and only for the principals that carry a project restriction and a
    /// scope (an API key, a mapped MCP caller): the projects the caller may read, where an API key without a project
    /// list reaches its owner's projects, and among them the ones it may work ad hoc in. Every other principal (a browser
    /// session, a REST bearer token) gets no projects and no key id, and so does a call without an HTTP request behind it:
    /// there is no caller to authorize. A failure leaves the context without projects (fail closed) and marks it
    /// <see cref="IProjectContext.AccessUndetermined"/>: the call is answered with a retry message and audited, and a tool
    /// list fails rather than listing fewer tools.
    /// </summary>
    public static async Task PopulateAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        // Without the MCP services there is no context to fill (and no tool to read one).
        var context = services.GetService<McpProjectContext>();
        if (context == null)
        {
            return;
        }

        context.AllowedProjectIds = [];
        context.QueryProjectIds = [];
        context.UserId = null;
        context.ApiKeyId = null;
        context.AccessUndetermined = false;

        var user = services.GetService<IHttpContextAccessor>()?.HttpContext?.User;
        if (user == null)
        {
            return;
        }

        try
        {
            var actor = await services.GetRequiredService<IBeaconActorAccessor>().GetCurrentAsync(cancellationToken);
            context.UserId = actor.BeaconUserId;

            // The key id and the projects are honoured only for the principals that mint them. Any other principal
            // carrying these claim types got them from elsewhere and is not trusted with them.
            if (!BeaconScopes.IsScopedCaller(user))
            {
                return;
            }

            context.ApiKeyId = int.TryParse(user.FindFirst(McpCallerClaimTypes.ApiKeyId)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var apiKeyId)
                ? apiKeyId
                : null;

            var scope = await services.GetRequiredService<IProjectAccessEvaluator>().GetScopeAsync(cancellationToken);
            var readable = scope.For(ProjectAction.Read);
            var queryable = scope.For(ProjectAction.Query);
            var live = readable.Ids == null || queryable.Ids == null
                ? await LiveProjectIdsAsync(services, cancellationToken)
                : [];
            var allowed = ProjectIdsOf(scope, readable, ProjectAction.Read, live);
            context.AllowedProjectIds = allowed;
            context.QueryProjectIds = ProjectIdsOf(scope, queryable, ProjectAction.Query, live)
                .Where(allowed.Contains)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.AllowedProjectIds = [];
            context.QueryProjectIds = [];
            context.AccessUndetermined = true;
            services.GetService<ILoggerFactory>()?
                .CreateLogger(typeof(ProjectContextFactory))
                .LogError(ex, "MCP project access could not be resolved for user {UserId}; the call is refused and reaches no project.", context.UserId);
        }
    }

    // The projects the caller may take the action in, as ids. A set that names no projects reaches every live project (a
    // key without a project list where nothing is restricted): the live projects the scope allows it in are listed.
    private static List<int> ProjectIdsOf(ProjectAccessScope scope, ProjectSet set, ProjectAction action, List<int> live)
    {
        if (set.Ids != null)
        {
            return set.Ids.ToList();
        }

        return live
            .Where(x => scope.Allows(x, action))
            .ToList();
    }

    private static async Task<List<int>> LiveProjectIdsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var contextFactory = services.GetRequiredService<IDbContextFactory<BeaconContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Projects
            .Select(x => x.Id)
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
    }
}
