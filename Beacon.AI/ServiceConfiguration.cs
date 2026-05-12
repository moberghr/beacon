using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.Ai;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.DbtIntegration;
using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.GitHub;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;

using Beacon.AI.Services.Mcp;
using Beacon.AI.Services.SemanticSearch;

namespace Beacon.AI;

public static class ServiceConfiguration
{
    /// <summary>
    /// Adds AI services to the service collection.
    /// LLM configuration in appsettings.json is optional — it can be configured later via Admin Settings.
    /// </summary>
    public static IServiceCollection AddBeaconAI(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Load LLM configuration from appsettings (optional — defaults used if not present)
        var llmConfig = configuration.GetSection("Beacon:LLM").Get<LlmConfiguration>()
                        ?? new LlmConfiguration();

        services.AddSingleton(llmConfig);

        // MediatR for AI handlers
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ServiceConfiguration).Assembly));

        // LLM Provider Factory
        services.AddSingleton<LlmProviderFactory>();

        // LLM Provider Manager (holds swappable provider, implements ILlmConfigurationUpdater)
        services.AddSingleton<LlmProviderManager>();
        services.AddSingleton<ILlmConfigurationUpdater>(sp => sp.GetRequiredService<LlmProviderManager>());

        // Connection tester for the admin settings "Test connection" button
        services.AddSingleton<ILlmConnectionTester, Handlers.AdminSettings.LlmConnectionTester>();

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
        services.TryAddScoped<IAiAlertGenerationService, AiAlertGenerationService>();

        // AI Actor services (autonomous monitoring)
        services.TryAddScoped<AiActorService>();
        services.TryAddScoped<IAiActorServiceExtended>(sp => sp.GetRequiredService<AiActorService>());
        services.TryAddScoped<Core.Services.IAiActorService>(sp => sp.GetRequiredService<AiActorService>());

        // GitHub Scanner
        services.TryAddTransient<GitHubApiClient>();
        services.AddTransient<ICodeAnalyzer, CSharpCodeAnalyzer>();
        services.TryAddTransient<IGitHubScannerService, GitHubScannerService>();

        // Knowledge Graph
        services.TryAddTransient<IKnowledgeGraphService, KnowledgeGraphService>();

        // Project Documentation (living knowledge base)
        services.TryAddTransient<IProjectDocumentationService, ProjectDocumentationService>();

        // Semantic Search
        services.TryAddTransient<ISemanticSearchService, SemanticSearchService>();

        // dbt Integration
        services.TryAddTransient<IDbtIntegrationService, DbtIntegrationService>();

        // MCP Learning Aggregation
        services.TryAddTransient<Core.Services.IMcpLearningAggregationService, Services.Learning.McpLearningAggregationService>();

        // MCP pipeline services (used by ProjectAskTool orchestrator)
        services.TryAddTransient<IIntentClassifier, IntentClassifier>();
        services.TryAddTransient<IDataSourceRouter, DataSourceRouter>();
        services.TryAddTransient<ISqlGenerationService, SqlGenerationService>();
        services.TryAddTransient<IKnowledgeAnswerService, KnowledgeAnswerService>();

        return services;
    }
}
