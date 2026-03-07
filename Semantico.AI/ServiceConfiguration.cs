using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Semantico.AI.Models.Configuration;
using Semantico.AI.Services.Ai;
using Semantico.AI.Services.Ai.AiActor;
using Semantico.AI.Services.Ai.DocumentationAgent;
using Semantico.AI.Services.Ai.MultiAgent;
using Semantico.AI.Services.DbtIntegration;
using Semantico.AI.Services.GitHub;
using Semantico.AI.Services.Knowledge;
using Semantico.AI.Services.LlmProviders;
using Semantico.AI.Services.Reports;
using Semantico.AI.Services.SchemaChangeDetection;
using Semantico.AI.Services.SemanticSearch;
using Semantico.Core.Services;

namespace Semantico.AI;

public static class ServiceConfiguration
{
    /// <summary>
    /// Adds AI services to the service collection.
    /// LLM configuration in appsettings.json is optional — it can be configured later via Admin Settings.
    /// </summary>
    public static IServiceCollection AddSemanticoAI(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Load LLM configuration from appsettings (optional — defaults used if not present)
        var llmConfig = configuration.GetSection("Semantico:LLM").Get<LlmConfiguration>()
                        ?? new LlmConfiguration();

        services.AddSingleton(llmConfig);

        // MediatR for AI handlers
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ServiceConfiguration).Assembly));

        // LLM Provider Factory
        services.AddSingleton<LlmProviderFactory>();

        // LLM Provider Manager (holds swappable provider, implements ILlmConfigurationUpdater)
        services.AddSingleton<LlmProviderManager>();
        services.AddSingleton<ILlmConfigurationUpdater>(sp => sp.GetRequiredService<LlmProviderManager>());

        // LLM Provider — delegating proxy that always uses the latest provider
        services.AddSingleton<ILlmProvider, DelegatingLlmProvider>();

        // Request queue for rate limiting
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<LlmConfiguration>();
            return new LlmRequestQueue(
                config.Limits.MaxConcurrentRequests);
        });

        // AI services
        services.TryAddScoped<IAiDocumentationService, AiDocumentationService>();
        services.TryAddScoped<IAiAlertGenerationService, AiAlertGenerationService>();

        // Multi-agent documentation service
        services.TryAddScoped<IMultiAgentDocumentationService, MultiAgentDocumentationService>();

        // Documentation Agent services (new agent-based approach)
        services.TryAddTransient<DocumentationAgentTools>();
        services.TryAddTransient<IDocumentationAgentService, DocumentationAgentService>();

        // AI Actor services (autonomous monitoring)
        // Register the concrete implementation
        services.TryAddScoped<AiActorService>();

        // Register both interfaces to the same implementation instance
        services.TryAddScoped<IAiActorServiceExtended>(sp => sp.GetRequiredService<AiActorService>());
        services.TryAddScoped<Core.Services.IAiActorService>(sp => sp.GetRequiredService<AiActorService>());

        // GitHub Scanner
        services.TryAddTransient<GitHubApiClient>();
        services.AddTransient<ICodeAnalyzer, CSharpCodeAnalyzer>();
        services.TryAddTransient<IGitHubScannerService, GitHubScannerService>();

        // Knowledge Graph
        services.TryAddTransient<IKnowledgeGraphService, KnowledgeGraphService>();

        // Schema Change Detection
        services.TryAddTransient<ISchemaChangeDetectionService, SchemaChangeDetectionService>();

        // Project Reports
        services.TryAddTransient<IProjectReportService, ProjectReportService>();

        // Semantic Search
        services.TryAddTransient<ISemanticSearchService, SemanticSearchService>();

        // dbt Integration
        services.TryAddTransient<IDbtIntegrationService, DbtIntegrationService>();

        return services;
    }
}
