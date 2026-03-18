using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Semantico.Core.Services;
using Semantico.MCP.Protocol;
using Semantico.MCP.Resources;
using Semantico.MCP.Services;
using Semantico.MCP.Tools;

namespace Semantico.MCP;

public static class ServiceConfiguration
{
    /// <summary>
    /// Adds Semantico MCP server services.
    /// Prerequisites: Semantico.Core and Semantico.AI services must be registered first.
    /// </summary>
    public static IServiceCollection AddSemanticoMcp(this IServiceCollection services)
    {
        // Protocol infrastructure
        services.TryAddSingleton<McpSessionManager>();
        services.TryAddTransient<McpRequestHandler>();

        // MCP Tools (project-centric)
        services.AddTransient<IMcpTool, GetContextTool>();
        services.AddTransient<IMcpTool, ProjectAskTool>();
        services.AddTransient<IMcpTool, ProjectQueryTool>();
        services.AddTransient<IMcpTool, ProjectGetDocumentationTool>();
        services.AddTransient<IMcpTool, ProjectSearchTool>();

        // MCP Resources
        services.AddTransient<IMcpResource, ProjectResources>();

        // Playground (public facade for UI)
        services.TryAddTransient<IMcpPlaygroundService, McpPlaygroundService>();

        // Audit
        services.TryAddTransient<McpAuditService>();

        return services;
    }
}
