using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.AspNetCore;
using Beacon.Core.Services;
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

        // SQL schema validator (pre-execution column check)
        services.AddSingleton<SqlSchemaValidator>();

        // AST read-only validator is registered by Beacon.Core (relocated to Beacon.Core.Services.Validation, §1.5)

        // Query execution
        services.TryAddTransient<IQueryExecutionService, QueryExecutionService>();

        // Cross-source query service
        services.TryAddTransient<ICrossSourceQueryService, CrossSourceQueryService>();

        // Audit & Learning
        services.TryAddTransient<McpAuditService>();
        services.TryAddTransient<McpSignalService>();

        // MCP Server via official SDK
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "Beacon", Version = "2.0.5" };
                options.ServerInstructions =
                    "Beacon gives agents governed access to a project's data sources.\n" +
                    "Recommended workflow:\n" +
                    "1. get_context — project overview: data sources, table counts, documentation status.\n" +
                    "2. search — find tables, columns, and docs by keyword before writing SQL.\n" +
                    "3. get_documentation — schema detail for a data source, table, or API endpoint.\n" +
                    "4. query — run read-only SQL (SELECT/WITH only) against one data source; results are row-capped and PII-masked (when PII detection is enabled).\n" +
                    "5. ask — natural-language question; Beacon routes to the right source(s), generates schema-grounded SQL, executes, and appends a _signal_id: N_ marker.\n" +
                    "6. feedback — after you verify an ask answer, report verdict 'correct' or 'incorrect' with that signal_id; correct answers become verified examples that improve future generation.\n" +
                    "Auth: API keys need the Execute or Admin scope for this endpoint. Keys can be project-restricted — pass project_id on every call when your key has access to more than one project.\n" +
                    "SQL dialect follows the target data source's engine (PostgreSQL, SQL Server, MySQL, BigQuery, Snowflake, Databricks). Write statements are rejected at multiple layers; don't attempt them.";
            })
            .WithHttpTransport(options =>
            {
                // Hybrid serving (SDK 2.2): legacy initialize-handshake clients get a session;
                // stateless-protocol (2026-07-28) clients are served without one, so any instance
                // behind a plain load balancer can answer any request.
                options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients;
                options.IdleTimeout = TimeSpan.FromMinutes(30);
            })
            .WithToolsFromAssembly(typeof(ServiceConfiguration).Assembly)
            .WithRequestFilters(x => x.AddListToolsFilter(McpToolDescriptionOverrides.CreateListToolsFilter()));

        // Playground (public facade for UI)
        services.TryAddTransient<IMcpPlaygroundService, McpPlaygroundService>();

        return services;
    }
}
