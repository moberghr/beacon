using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Authorization;
using Beacon.Core.Services;
using Beacon.Core.SavedQueries;
using Beacon.MCP.Discovery;
using Beacon.MCP.SavedQueries;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;

namespace Beacon.MCP;

public static class ServiceConfiguration
{
    /// <summary>
    /// Adds Beacon MCP server services using the official ModelContextProtocol SDK, with all eight built-in tools and the
    /// saved-query tools. Prerequisites: Beacon.Core and Beacon.AI services must be registered first.
    /// </summary>
    public static IServiceCollection AddBeaconMcp(this IServiceCollection services)
    {
        return services.AddBeaconMcp(_ => { });
    }

    /// <summary>
    /// Adds Beacon MCP server services using the official ModelContextProtocol SDK, serving the built-in tools and the
    /// saved-query tools <paramref name="configure"/> chooses (<see cref="BeaconMcpOptions"/>). Prerequisites:
    /// Beacon.Core and Beacon.AI services must be registered first.
    /// </summary>
    public static IServiceCollection AddBeaconMcp(this IServiceCollection services, Action<BeaconMcpOptions> configure)
    {
        var mcpOptions = new BeaconMcpOptions();
        configure(mcpOptions);
        mcpOptions.Validate();
        services.AddSingleton(mcpOptions);

        // Project context infrastructure
        services.AddScoped<McpProjectContext>();
        services.AddScoped<IProjectContext>(ProjectContextFactory.Create);
        services.AddHttpContextAccessor();
        // Core reads the request's caller through it; replaces only Core's off-request default, so a host principal stays.
        services.TryUseRequestPrincipal<Beacon.Shared.Http.HttpContextRequestPrincipal>();

        // Register tool classes directly for playground access
        services.AddScoped<GetContextTool>();
        services.AddScoped<ProjectAskTool>();
        services.AddScoped<ProjectQueryTool>();
        services.AddScoped<ProjectGetDocumentationTool>();
        services.AddScoped<ProjectSearchTool>();
        services.AddScoped<FeedbackTool>();
        services.AddScoped<DryRunTool>();
        services.AddScoped<GetQueryContextTool>();

        // SQL schema validator is registered by Beacon.Core (moved to Beacon.Core.Services.Validation, §2.4)
        // AST read-only validator is registered by Beacon.Core (relocated to Beacon.Core.Services.Validation, §1.5)

        // Query execution
        services.TryAddTransient<IQueryExecutionService, QueryExecutionService>();

        // MaxConcurrentQueriesPerKey: one in-process count per instance, shared by every request.
        services.TryAddSingleton<McpQueryConcurrencyLimiter>();

        // Ask SQL pipeline executor seam: adapts IQueryExecutionService onto IAskSqlPipeline's execution
        // interface (§ Architecture ①) so ProjectAskTool never touches the pipeline's execution details.
        services.TryAddTransient<IAskSqlExecutor, AskSqlExecutor>();

        // Cross-source query service
        services.TryAddTransient<ICrossSourceQueryService, CrossSourceQueryService>();

        // Audit & Learning
        services.TryAddTransient<McpAuditService>();
        services.TryAddScoped<McpAuditOutcome>();
        services.TryAddTransient<McpSignalService>();

        // Approved saved queries as q_<name> tools (or search_saved_queries / run_saved_query past the limit), served
        // through the SDK's list/call handlers next to the attribute tools; AddHostEndpointTools chains after these.
        if (mcpOptions.SavedQueryTools)
        {
            services.TryAddTransient<SavedQueryToolService>();
            services.Configure<McpServerOptions>(InstallSavedQueryToolHandlers);
        }

        // MCP Server via official SDK
        var server = services
            .AddMcpServer(options =>
            {
                options.ServerInstructions = mcpOptions.ServerInstructions ??
                    "Beacon gives agents governed access to a project's data sources.\n" +
                    "Recommended workflow for writing your own SQL:\n" +
                    "1. get_context — project overview: data sources, table counts, documentation status.\n" +
                    "2. search — find tables, columns, and docs by keyword before writing SQL.\n" +
                    "3. get_query_context — grounding for a specific question: table schemas with real sample values, verified join paths, human-verified example queries, and the business glossary. Use this before writing SQL by hand.\n" +
                    "4. dry_run — validate your SQL through every safety gate (read-only guardrail, AST, schema columns, provider EXPLAIN) without executing it.\n" +
                    "5. query — run the validated read-only SQL (SELECT/WITH only); results are row-capped and PII-masked (when PII detection is enabled).\n" +
                    "Or let Beacon do it: ask — natural-language question; Beacon routes to the right source(s), generates schema-grounded SQL, executes, and appends a _signal_id: N_ marker.\n" +
                    "Close the loop: feedback — after you verify an ask answer, report verdict 'correct' or 'incorrect' with that signal_id; verdicts are recorded for an Admin to review, and an Admin may promote a correct answer to a verified example that improves future generation.\n" +
                    "get_documentation gives deeper schema/lineage detail for a data source, table, or API endpoint, and lists/reads the documents the host application ships (document=<path>).\n" +
                    "get_context with format='agents_md' returns a deterministic project brief for an agent workspace's AGENTS.md.\n" +
                    "Approved saved queries appear as q_<name> tools (or, for many, search_saved_queries to find one and run_saved_query to run it): reviewed, versioned, parameterized read-only SQL — prefer one over hand-written SQL when it answers the question.\n" +
                    "When the host application exposes some of its read-only endpoints, they appear as api_<name> tools (or, for many endpoints, search_api to find one and call_api to run it); they run as your host identity, and the host decides what each may do.\n" +
                    "Auth: API keys need the Execute scope for this endpoint. Keys can be project-restricted — pass project_id on every call when your key has access to more than one project.\n" +
                    "SQL dialect follows the target data source's engine (PostgreSQL, SQL Server, MySQL, BigQuery, Snowflake, Databricks). Write statements are rejected at multiple layers; don't attempt them.";
            })
            .WithHttpTransport(options =>
            {
                // Hybrid serving (SDK 2.2): stateless-protocol (2026-07-28) clients are served
                // without a session, so for THOSE clients any instance behind a plain load balancer
                // can answer any request. Legacy initialize-handshake clients still get an
                // Mcp-Session-Id and their follow-up requests must land on the instance holding
                // that session — multi-instance deployments need session affinity for them.
                options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients;
                options.IdleTimeout = TimeSpan.FromMinutes(30);
            });

        // Clients are told at initialize the name the discovery documents publish: the host's ResourceName when set.
        services.AddOptions<McpServerOptions>()
            .Configure<IServiceProvider>((options, provider) =>
            {
                var configuration = provider.GetService<IConfiguration>();
                var resourceName = configuration == null
                    ? null
                    : McpDiscoveryEndpoints.GetProtectedResourceOptions(provider, configuration).ResourceName;
                options.ServerInfo = new() { Name = McpDiscoveryDocuments.ServerNameFor(resourceName), Version = McpDiscoveryDocuments.ServerVersion };
            });

        // Only the built-in tools the host serves exist; one left out is neither listed nor callable.
        foreach (var tool in mcpOptions.BuiltInTools)
        {
            BuiltInToolRegistrations[tool](server);
        }

        server
            // Scope enforcement (§1.4) first: every request of a refused caller but tools/call, initialize and ping
            // is refused here; tools/call by the scope call-tool filter, ahead of every tool so a refused call reaches
            // none. The project-context filters run outermost: they only read the caller's project access, which the
            // tools, the tool lists and the audit of a refused call need. A call whose project access could not be
            // determined is answered (and audited) innermost, after the scope check, before any tool.
            .WithMessageFilters(x => x.AddIncomingFilter(McpScopeMessageFilter.Create()))
            .WithRequestFilters(x => x
                .AddListToolsFilter(McpProjectContextFilter.CreateListToolsFilter())
                .AddListToolsFilter(McpToolDescriptionOverrides.CreateListToolsFilter())
                .AddCallToolFilter(McpProjectContextFilter.CreateCallToolFilter())
                .AddCallToolFilter(McpScopeCallToolFilter.Create())
                .AddCallToolFilter(McpAuditCallToolFilter.Create())
                .AddCallToolFilter(McpProjectContextFilter.CreateUndeterminedAccessCallToolFilter()));

        // Playground (public facade for UI)
        services.TryAddTransient<IMcpPlaygroundService, McpPlaygroundService>();

        // Notes at startup that provisioned user callers get the Viewer role (catalog only) by default.
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, McpUserCallerStartupNote>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, McpAuditStartupWarning>());

        return services;
    }

    // One registration per built-in tool, by the name its [McpServerTool] declares (McpToolCatalog pins the names).
    private static readonly IReadOnlyDictionary<string, Func<IMcpServerBuilder, IMcpServerBuilder>> BuiltInToolRegistrations =
        new Dictionary<string, Func<IMcpServerBuilder, IMcpServerBuilder>>(StringComparer.Ordinal)
        {
            ["get_context"] = x => x.WithTools<GetContextTool>(),
            ["ask"] = x => x.WithTools<ProjectAskTool>(),
            ["query"] = x => x.WithTools<ProjectQueryTool>(),
            ["get_documentation"] = x => x.WithTools<ProjectGetDocumentationTool>(),
            ["search"] = x => x.WithTools<ProjectSearchTool>(),
            ["feedback"] = x => x.WithTools<FeedbackTool>(),
            ["dry_run"] = x => x.WithTools<DryRunTool>(),
            ["get_query_context"] = x => x.WithTools<GetQueryContextTool>()
        };

    /// <summary>
    /// Adds the saved-query tools through the SDK's list/call handlers, chaining any handler already installed. The
    /// tools need Core's <see cref="ISavedQueryToolSource"/>; a host without it simply gets none.
    /// </summary>
    private static void InstallSavedQueryToolHandlers(McpServerOptions options)
    {
        var previousList = options.Handlers.ListToolsHandler;
        var previousCall = options.Handlers.CallToolHandler;

        options.Handlers.ListToolsHandler = async (request, cancellationToken) =>
        {
            var result = previousList != null
                ? await previousList(request, cancellationToken)
                : new ListToolsResult();

            var service = ResolveSavedQueryToolService(request.Services);
            if (service == null)
            {
                return result;
            }

            var tools = await service.ListToolsAsync(cancellationToken);
            if (tools.Count > 0)
            {
                result.Tools = [.. result.Tools ?? [], .. tools];
            }

            return result;
        };

        options.Handlers.CallToolHandler = async (request, cancellationToken) =>
        {
            var name = request.Params?.Name;
            if (SavedQueryToolService.Handles(name))
            {
                var service = ResolveSavedQueryToolService(request.Services);
                if (service != null)
                {
                    return await service.CallAsync(name!, request.Params?.Arguments, cancellationToken);
                }
            }

            if (previousCall != null)
            {
                return await previousCall(request, cancellationToken);
            }

            throw new McpProtocolException($"Unknown tool: '{name}'", McpErrorCode.InvalidParams);
        };
    }

    private static SavedQueryToolService? ResolveSavedQueryToolService(IServiceProvider? services) =>
        services?.GetService<ISavedQueryToolSource>() == null
            ? null
            : services.GetService<SavedQueryToolService>();
}
