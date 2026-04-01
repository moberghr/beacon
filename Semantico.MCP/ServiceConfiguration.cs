using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;
using Semantico.Core.Services;
using Semantico.MCP.Resources;
using Semantico.MCP.Services;
using Semantico.MCP.Tools;

namespace Semantico.MCP;

public static class ServiceConfiguration
{
    /// <summary>
    /// Adds Semantico MCP server services using the official ModelContextProtocol SDK.
    /// Prerequisites: Semantico.Core and Semantico.AI services must be registered first.
    /// </summary>
    public static IServiceCollection AddSemanticoMcp(this IServiceCollection services)
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
                options.ServerInfo = new() { Name = "Semantico", Version = "2.0.5" };
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
