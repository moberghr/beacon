using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.GitHub;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;

using Beacon.AI.Services.Mcp;
using Beacon.Core;

namespace Beacon.AI;

public static class ServiceConfiguration
{
    /// <summary>
    /// Adds Beacon's AI services and switches AI on (<see cref="Core.BeaconConfiguration.UseAI"/>): the stand-ins Core
    /// registered while AI was off are removed, so the real implementations are the ones resolved. LLM configuration in
    /// appsettings.json is optional — it can be configured later via Admin Settings.
    /// </summary>
    public static BeaconBuilder AddAI(this BeaconBuilder builder)
    {
        return builder.AddAIThroughChain(switchAiOn: true);
    }

    /// <summary>
    /// The MCP gateway's AI: Beacon's AI services, with AI left off (<see cref="Core.BeaconConfiguration.UseAI"/> false) and
    /// the AI-off stand-ins kept, so the evaluation harness and pattern replay refuse as they always have on a gateway.
    /// </summary>
    internal static BeaconBuilder AddAIKeepingAiOff(this BeaconBuilder builder)
    {
        return builder.AddAIThroughChain(switchAiOn: false);
    }

    internal static BeaconBuilder AddAIThroughChain(this BeaconBuilder builder, bool switchAiOn)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Options
            ?? throw new InvalidOperationException("AddAI() needs the builder services.AddBeacon(...) returns.");
        if (builder.AiAdded || options.UseAI || builder.Services.Any(x => x.ServiceType == typeof(LlmConfiguration)))
        {
            throw new InvalidOperationException(
                "Beacon's AI services are already registered: call .AddAI() once, and not beside AddBeaconAI or UseAI = true.");
        }

        if (switchAiOn)
        {
            options.UseAI = true;
            Core.Services.AiDisabled.RemoveStandIns(builder.Services);
        }

        builder.Services.AddBeaconAIServices(builder.Configuration);
        builder.AiAdded = true;
        BeaconBuilder.MarkChain(builder.Services);

        return builder;
    }

    /// <summary>
    /// Adds AI services to the service collection.
    /// LLM configuration in appsettings.json is optional — it can be configured later via Admin Settings.
    /// </summary>
    /// <remarks>
    /// The fluent chain is the preferred way to wire Beacon: <see cref="AddAI"/> on the builder <c>services.AddBeacon(...)</c>
    /// returns, which also switches AI on. This method stays supported and becomes obsolete in a later release.
    /// </remarks>
    public static IServiceCollection AddBeaconAI(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (BeaconBuilder.UsesChain(services))
        {
            throw new InvalidOperationException(
                "This host is wired with the chain (services.AddBeacon(...), .AddAI(), .AddMcp()): add AI with .AddAI() on its builder, not AddBeaconAI.");
        }

        return services.AddBeaconAIServices(configuration);
    }

    internal static IServiceCollection AddBeaconAIServices(
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
        // DelegatingLlmProvider can resolve it. Built from the bootstrap config: its queue-wait limits stay those, while
        // the manager hands it the concurrency and rate limits of every configuration an Admin saves.
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<LlmConfiguration>();
            return new LlmRequestQueue(config.Limits, sp.GetService<TimeProvider>(), sp.GetService<ILogger<LlmRequestQueue>>());
        });

        // LLM Provider Manager (holds swappable provider, implements ILlmConfigurationUpdater).
        // Registered before the factory's accessor closure resolves it lazily.
        services.AddSingleton(sp => new LlmProviderManager(
            sp.GetRequiredService<LlmConfiguration>(),
            sp.GetRequiredService<LlmProviderFactory>(),
            sp.GetRequiredService<LlmRequestQueue>(),
            sp.GetRequiredService<ILogger<LlmProviderManager>>()));
        services.AddSingleton<ILlmConfigurationUpdater>(sp => sp.GetRequiredService<LlmProviderManager>());

        // Connection tester for the admin settings "Test connection" button
        services.AddSingleton<ILlmConnectionTester, Handlers.AdminSettings.LlmConnectionTester>();

        // The providers this package ships, each sending through its named HTTP client. Provider packages add theirs
        // (Moberg.Beacon.AI.Bedrock: AddBeaconBedrock()); a host's own registration for a built-in provider wins.
        services.AddLlmHttpClients();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILlmProviderRegistration, OpenAiProviderRegistration>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILlmProviderRegistration, ClaudeProviderRegistration>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILlmProviderRegistration, AzureOpenAiProviderRegistration>());

        // LLM Provider Factory — reads the CURRENT configuration from the manager on every
        // CreateProvider() call so hot-swapped settings are picked up. Falls back to the
        // bootstrap LlmConfiguration before the manager is constructed (first resolution).
        services.AddSingleton(sp =>
        {
            var bootstrap = sp.GetRequiredService<LlmConfiguration>();
            return new LlmProviderFactory(
                () =>
                {
                    var manager = sp.GetService<LlmProviderManager>();
                    return manager is null ? bootstrap : manager.CurrentConfiguration;
                },
                sp.GetServices<ILlmProviderRegistration>(),
                sp.GetService<ILogger<LlmProviderFactory>>());
        });

        // The installed providers for the admin settings. Replace, not TryAdd: it removes Core's AI-off default when Core
        // registered first, and Core's TryAdd adds nothing when this runs first.
        services.Replace(ServiceDescriptor.Singleton<ILlmProviderCatalog, LlmProviderCatalog>());

        // LLM Provider — delegating proxy that always uses the latest provider and funnels
        // every completion through LlmRequestQueue.
        services.AddSingleton<ILlmProvider, DelegatingLlmProvider>();

        // The log of row-derived data sent to the LLM provider (ids and categories only).
        services.TryAddTransient<ILlmRowDataEgress, LlmRowDataEgress>();

        // AI Actor services (autonomous monitoring)
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddTransient<IAiActorThinkClaim, EfAiActorThinkClaim>();
        services.TryAddScoped<AiActorService>();
        services.TryAddScoped<Core.Services.IAiActorService>(sp => sp.GetRequiredService<AiActorService>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<Core.BackgroundTasks.IBackgroundTaskExecutor, AiActorThinkTaskExecutor>());

        // GitHub Scanner
        services.AddOptions<GitHubScanOptions>()
            .Bind(configuration.GetSection(GitHubScanOptions.SectionName))
            .Validate(
                x => x.TaskDeadline > TimeSpan.Zero && x.TaskDeadline <= TimeSpan.FromHours(GitHubScanOptions.MaxTaskDeadlineHours),
                $"{GitHubScanOptions.SectionName}:TaskDeadline must be more than zero and at most {GitHubScanOptions.MaxTaskDeadlineHours} hours.")
            .ValidateOnStart();
        services.TryAddTransient<GitHubApiClient>();
        // Analyzers are a collection (the scanner takes every one): a host's own analyzer is added alongside this one.
        services.TryAddEnumerable(ServiceDescriptor.Transient<ICodeAnalyzer, CSharpCodeAnalyzer>());
        services.TryAddTransient<IGitHubScannerService, GitHubScannerService>();
        services.TryAddEnumerable(ServiceDescriptor.Transient<Beacon.Core.BackgroundTasks.IBackgroundTaskExecutor, Handlers.Projects.RepositoryScanTaskExecutor>());

        // Disabled by default; Moberg.Beacon.AI.Embeddings.Onnx replaces it through AddBeaconOnnxEmbeddings().
        services.TryAddSingleton<IBeaconEmbeddingService, DisabledEmbeddingService>();

        // Knowledge Graph. Grounding a prompt leaves out is logged once per row and interval, across requests.
        services.TryAddSingleton<WithheldGroundingLog>();
        services.TryAddTransient<IKnowledgeGraphService, KnowledgeGraphService>();

        // Ask-time value grounding (spec item 5) — probes candidate string columns for literal-looking
        // words in the question, fail-closed, gated by EnableValueGrounding.
        services.TryAddTransient<IValueGroundingService, ValueGroundingService>();
        // The per-credential slots its probes may take beside the call; Beacon.MCP serves an MCP caller's. None by default.
        services.TryAddScoped<IProbeConcurrencyLeases, NoProbeConcurrencyLeases>();

        // Project Documentation (living knowledge base)
        services.TryAddTransient<IProjectDocumentationService, ProjectDocumentationService>();
        services.TryAddEnumerable(ServiceDescriptor.Transient<Beacon.Core.BackgroundTasks.IBackgroundTaskExecutor, DocumentationGenerationTaskExecutor>());

        // MCP LLM lesson extractor (LLM-primary schema-correction detection; regex fallback lives in the
        // aggregation service). Queue-backed via DelegatingLlmProvider (§6.1); provider-agnostic (§9.4).
        services.TryAddTransient<Core.Services.ILessonExtractor, Services.Learning.LlmLessonExtractor>();

        // MCP replay-verification gate (§ Architecture ⑥). Promotes NeedsEvidence candidates only when they
        // measurably help against the golden set; executes SQL read-only via IMcpEvalService (no new path).
        services.TryAddTransient<Core.Services.IPatternReplayVerifier, Services.Learning.PatternReplayVerifier>();

        // MCP Learning Aggregation (resolves the optional ILessonExtractor + IPatternReplayVerifier via its ctor).
        services.TryAddTransient<Core.Services.IMcpLearningAggregationService, Services.Learning.McpLearningAggregationService>();

        // Writes the DB-managed pgvector column that the PostgreSQL nearest-neighbor search orders by
        // (the column is invisible to the EF model, so both indexing services set it through this seam).
        services.TryAddSingleton<Services.Embeddings.IEmbeddingVectorColumnWriter, Services.Embeddings.PgVectorColumnWriter>();

        // MCP Embedding Indexing (populates McpEmbedding for hybrid retrieval + semantic few-shot)
        services.TryAddTransient<Core.Services.IEmbeddingIndexingService, Services.Embeddings.EmbeddingIndexingService>();

        // MCP Doc-chunk Indexing (Tier-3 ⑨/⑩): chunks + optional contextual blurb + embeds project docs into
        // McpDocChunk + McpEmbedding. Interface in Core, impl here — mirrors the embedding-indexing split.
        services.TryAddTransient<Core.Services.IDocChunkIndexingService, Services.Knowledge.DocChunkIndexingService>();

        // MCP Eval harness (interface in Core, impl here; wired at composition root like the learning
        // aggregation split). Executes SQL strictly read-only via the Core provider factory.
        services.TryAddTransient<Core.Services.IMcpEvalService, Services.Eval.McpEvalService>();

        // MCP pipeline services (used by ProjectAskTool orchestrator)
        services.TryAddTransient<IIntentClassifier, IntentClassifier>();
        services.TryAddTransient<IDataSourceRouter, DataSourceRouter>();
        // SqlGenerationService's optional TimeProvider ctor param resolves from Beacon.Core's
        // TryAddSingleton<TimeProvider>(TimeProvider.System) registration when the host wires both
        // AddBeaconCore and AddBeaconAI; the ctor default (TimeProvider.System) only matters for
        // standalone construction (e.g. tests), so no separate registration is needed here.
        services.TryAddTransient<ISqlGenerationService, SqlGenerationService>();
        services.TryAddTransient<IKnowledgeAnswerService, KnowledgeAnswerService>();

        // Shared generate → validate → repair → execute core (§ Architecture ①). Both the MCP `ask` tool
        // and the eval harness run this SAME instance type with the SAME repair budget (SC1).
        services.TryAddTransient<IAskSqlPipeline, AskSqlPipeline>();

        return services;
    }

    /// <summary>
    /// Registers the disabled embedding service (<c>TryAdd</c>, so an existing registration wins).
    /// Local embeddings now live in the <c>Moberg.Beacon.AI.Embeddings.Onnx</c> package.
    /// </summary>
    [Obsolete("Embeddings are no longer part of Moberg.Beacon.AI. Use AddBeaconOnnxEmbeddings() from Moberg.Beacon.AI.Embeddings.Onnx. This method now registers a disabled service.")]
    public static IServiceCollection AddBeaconEmbeddings(this IServiceCollection services)
    {
        services.TryAddSingleton<IBeaconEmbeddingService, DisabledEmbeddingService>();
        return services;
    }
}
