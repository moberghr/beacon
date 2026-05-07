using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;
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
        services.AddSingleton<McpProjectContextManager>();
        services.AddScoped<McpProjectContext>();
        services.AddScoped<IProjectContext>(ProjectContextFactory.Create);
        services.AddHttpContextAccessor();

        // Register tool classes directly for playground access
        services.AddScoped<GetContextTool>();
        services.AddScoped<ProjectAskTool>();
        services.AddScoped<ProjectQueryTool>();
        services.AddScoped<ProjectGetDocumentationTool>();
        services.AddScoped<ProjectSearchTool>();

        // SQL schema validator (pre-execution column check)
        services.AddSingleton<SqlSchemaValidator>();

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
            })
            .WithHttpTransport(options =>
            {
                options.IdleTimeout = TimeSpan.FromMinutes(30);
            })
            .WithToolsFromAssembly(typeof(ServiceConfiguration).Assembly)
            .WithResourcesFromAssembly(typeof(ServiceConfiguration).Assembly);

        // Playground (public facade for UI)
        services.TryAddTransient<IMcpPlaygroundService, McpPlaygroundService>();

        return services;
    }
}
