using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.AI.Services.Mcp;
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
    /// Adds Beacon MCP server services using the official ModelContextProtocol SDK.
    /// Prerequisites: Beacon.Core and Beacon.AI services must be registered first.
    /// </summary>
    public static IServiceCollection AddBeaconMcp(this IServiceCollection services)
    {
        // Project context infrastructure
        services.AddScoped<McpProjectContext>();
        services.AddScoped<IProjectContext>(ProjectContextFactory.Create);
        services.AddHttpContextAccessor();

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

        // Ask SQL pipeline executor seam: adapts IQueryExecutionService onto IAskSqlPipeline's execution
        // interface (§ Architecture ①) so ProjectAskTool never touches the pipeline's execution details.
        services.TryAddTransient<IAskSqlExecutor, AskSqlExecutor>();

        // Cross-source query service
        services.TryAddTransient<ICrossSourceQueryService, CrossSourceQueryService>();

        // Audit & Learning
        services.TryAddTransient<McpAuditService>();
        services.TryAddTransient<McpSignalService>();

        // Approved saved queries as q_<name> tools (or search_saved_queries / run_saved_query past the limit), served
        // through the SDK's list/call handlers next to the attribute tools; AddHostEndpointTools chains after these.
        services.TryAddTransient<SavedQueryToolService>();
        services.Configure<McpServerOptions>(InstallSavedQueryToolHandlers);

        // MCP Server via official SDK
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = McpDiscoveryDocuments.ServerName, Version = McpDiscoveryDocuments.ServerVersion };
                options.ServerInstructions =
                    "Beacon gives agents governed access to a project's data sources.\n" +
                    "Recommended workflow for writing your own SQL:\n" +
                    "1. get_context — project overview: data sources, table counts, documentation status.\n" +
                    "2. search — find tables, columns, and docs by keyword before writing SQL.\n" +
                    "3. get_query_context — grounding for a specific question: table schemas with real sample values, verified join paths, human-verified example queries, and the business glossary. Use this before writing SQL by hand.\n" +
                    "4. dry_run — validate your SQL through every safety gate (read-only guardrail, AST, schema columns, provider EXPLAIN) without executing it.\n" +
                    "5. query — run the validated read-only SQL (SELECT/WITH only); results are row-capped and PII-masked (when PII detection is enabled).\n" +
                    "Or let Beacon do it: ask — natural-language question; Beacon routes to the right source(s), generates schema-grounded SQL, executes, and appends a _signal_id: N_ marker.\n" +
                    "Close the loop: feedback — after you verify an ask answer, report verdict 'correct' or 'incorrect' with that signal_id; correct answers become verified examples that improve future generation.\n" +
                    "get_documentation gives deeper schema/lineage detail for a data source, table, or API endpoint, and lists/reads the documents the host application ships (document=<path>).\n" +
                    "get_context with format='agents_md' returns a deterministic project brief for an agent workspace's AGENTS.md.\n" +
                    "Approved saved queries appear as q_<name> tools (or, for many, search_saved_queries to find one and run_saved_query to run it): reviewed, versioned, parameterized read-only SQL — prefer one over hand-written SQL when it answers the question.\n" +
                    "When the host application exposes some of its read-only endpoints, they appear as api_<name> tools (or, for many endpoints, search_api to find one and call_api to run it); they run with your host permissions.\n" +
                    "Auth: API keys need the Execute or Admin scope for this endpoint. Keys can be project-restricted — pass project_id on every call when your key has access to more than one project.\n" +
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
            })
            .WithToolsFromAssembly(typeof(ServiceConfiguration).Assembly)
            .WithRequestFilters(x => x.AddListToolsFilter(McpToolDescriptionOverrides.CreateListToolsFilter()));

        // Playground (public facade for UI)
        services.TryAddTransient<IMcpPlaygroundService, McpPlaygroundService>();

        return services;
    }

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
