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

        // Bootstrap configuration — manager owns the live snapshot after construction.
        services.AddSingleton(llmConfig);

        // MediatR for AI handlers
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ServiceConfiguration).Assembly));

        // Request queue for rate limiting — registered BEFORE the delegating provider so
        // DelegatingLlmProvider can resolve it. Uses bootstrap config; concurrency limit is
        // fixed for the singleton's lifetime (matches prior behaviour).
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<LlmConfiguration>();
            return new LlmRequestQueue(config.Limits.MaxConcurrentRequests);
        });

        // LLM Provider Manager (holds swappable provider, implements ILlmConfigurationUpdater).
        // Registered before the factory's accessor closure resolves it lazily.
        services.AddSingleton<LlmProviderManager>();
        services.AddSingleton<ILlmConfigurationUpdater>(sp => sp.GetRequiredService<LlmProviderManager>());

        // LLM Provider Factory — reads the CURRENT configuration from the manager on every
        // CreateProvider() call so hot-swapped settings are picked up. Falls back to the
        // bootstrap LlmConfiguration before the manager is constructed (first resolution).
        services.AddSingleton(sp =>
        {
            var bootstrap = sp.GetRequiredService<LlmConfiguration>();
            return new LlmProviderFactory(() =>
            {
                var manager = sp.GetService<LlmProviderManager>();
                return manager is null ? bootstrap : manager.CurrentConfiguration;
            });
        });

        // LLM Provider — delegating proxy that always uses the latest provider and funnels
        // every completion through LlmRequestQueue.
        services.AddSingleton<ILlmProvider, DelegatingLlmProvider>();

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
