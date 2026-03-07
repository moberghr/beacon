using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // MCP Tools
        services.AddTransient<IMcpTool, ListDataSourcesTool>();
        services.AddTransient<IMcpTool, ExecuteQueryTool>();
        services.AddTransient<IMcpTool, GetDocumentationTool>();
        services.AddTransient<IMcpTool, AskTool>();
        services.AddTransient<IMcpTool, SearchCatalogTool>();

        // MCP Resources
        services.AddTransient<IMcpResource, DataSourceResources>();
        services.AddTransient<IMcpResource, ProjectResources>();

        // Audit
        services.TryAddTransient<McpAuditService>();

        return services;
    }
}
