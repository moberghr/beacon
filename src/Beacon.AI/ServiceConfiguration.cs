using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.Ai;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.DbtIntegration;
using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.Embeddings;
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

        // Connection tester for the admin settings "Test connection" button
        services.AddSingleton<ILlmConnectionTester, Handlers.AdminSettings.LlmConnectionTester>();

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

        // Local ONNX embeddings (in-process, no egress). Singleton for session reuse.
        services.AddBeaconEmbeddings();

        // Knowledge Graph
        services.TryAddTransient<IKnowledgeGraphService, KnowledgeGraphService>();

        // Project Documentation (living knowledge base)
        services.TryAddTransient<IProjectDocumentationService, ProjectDocumentationService>();

        // Semantic Search
        services.TryAddTransient<ISemanticSearchService, SemanticSearchService>();

        // dbt Integration
        services.TryAddTransient<IDbtIntegrationService, DbtIntegrationService>();

        // MCP LLM lesson extractor (LLM-primary schema-correction detection; regex fallback lives in the
        // aggregation service). Queue-backed via DelegatingLlmProvider (§6.1); provider-agnostic (§9.4).
        services.TryAddTransient<Core.Services.ILessonExtractor, Services.Learning.LlmLessonExtractor>();

        // MCP replay-verification gate (§ Architecture ⑥). Promotes NeedsEvidence candidates only when they
        // measurably help against the golden set; executes SQL read-only via IMcpEvalService (no new path).
        services.TryAddTransient<Core.Services.IPatternReplayVerifier, Services.Learning.PatternReplayVerifier>();

        // MCP Learning Aggregation (resolves the optional ILessonExtractor + IPatternReplayVerifier via its ctor).
        services.TryAddTransient<Core.Services.IMcpLearningAggregationService, Services.Learning.McpLearningAggregationService>();

        // MCP Embedding Indexing (populates McpEmbedding for hybrid retrieval + semantic few-shot)
        services.TryAddTransient<Core.Services.IEmbeddingIndexingService, Services.Embeddings.EmbeddingIndexingService>();

        // MCP Eval harness (interface in Core, impl here; wired at composition root like the learning
        // aggregation split). Executes SQL strictly read-only via the Core provider factory.
        services.TryAddTransient<Core.Services.IMcpEvalService, Services.Eval.McpEvalService>();

        // MCP pipeline services (used by ProjectAskTool orchestrator)
        services.TryAddTransient<IIntentClassifier, IntentClassifier>();
        services.TryAddTransient<IDataSourceRouter, DataSourceRouter>();
        services.TryAddTransient<ISqlGenerationService, SqlGenerationService>();
        services.TryAddTransient<IKnowledgeAnswerService, KnowledgeAnswerService>();

        return services;
    }

    /// <summary>
    /// Registers the local ONNX embedding service. Kept as a discrete extension (per the plan's
    /// <c>AddBeaconEmbeddings()</c>) though wired via <see cref="AddBeaconAI"/> rather than
    /// <c>BeaconBuilder</c> — the AI layer registers through <c>AddBeaconAI(IServiceCollection)</c>,
    /// not the builder chain (that is used only for connectors and DB providers). Singleton so the
    /// <c>InferenceSession</c> is loaded once and reused (§6). <c>TryAdd</c> lets the host override.
    /// </summary>
    public static IServiceCollection AddBeaconEmbeddings(this IServiceCollection services)
    {
        services.TryAddSingleton<IBeaconEmbeddingService, OnnxEmbeddingService>();
        return services;
    }
}
