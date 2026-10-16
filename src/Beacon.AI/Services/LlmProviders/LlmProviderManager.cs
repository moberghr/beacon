using Beacon.AI.Models.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Settings;
using Beacon.Core.Services;
using Microsoft.Extensions.Logging;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// Manages the current LLM provider and supports hot-swapping when configuration changes.
/// The active <see cref="LlmConfiguration"/> is held in a single reference field that is
/// replaced atomically via <see cref="Interlocked.Exchange{T}"/>; readers always observe a
/// consistent snapshot.
/// </summary>
public class LlmProviderManager : ILlmConfigurationUpdater
{
    private readonly LlmProviderFactory _factory;
    private readonly ILogger<LlmProviderManager> _logger;
    private readonly object _lock = new();
    private LlmConfiguration _config;
    private ILlmProvider? _currentProvider;

    public LlmProviderManager(
        LlmConfiguration llmConfig,
        LlmProviderFactory factory,
        ILogger<LlmProviderManager> logger)
    {
        _config = llmConfig ?? throw new ArgumentNullException(nameof(llmConfig));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Create initial provider if configuration is valid
        if (!string.IsNullOrEmpty(_config.ApiKey) || _config.Provider == AiProvider.Bedrock)
        {
            try
            {
                _currentProvider = factory.CreateProvider();
            }
            catch (Exception ex)
            {
                // The failure's types and codes only: an SDK message can carry credential or profile details.
                _logger.LogWarning(
                    "Initial LLM provider construction failed for provider {Provider} ({Failure}). Starting with no active provider; configure via Admin Settings.",
                    _config.Provider,
                    FailureCodes.Describe(ex));
            }
        }
    }

    public ILlmProvider? CurrentProvider => Volatile.Read(ref _currentProvider);

    /// <summary>
    /// Snapshot of the currently active configuration. Always returns the most recently
    /// installed instance; never mutated in place.
    /// </summary>
    public LlmConfiguration CurrentConfiguration => Volatile.Read(ref _config);

    public void UpdateConfiguration(AppSettingsData settings)
    {
        lock (_lock)
        {
            var previous = _config;
            var next = previous with
            {
                Provider = settings.LlmProvider ?? previous.Provider,
                ApiKey = settings.LlmApiKey ?? string.Empty,
                Endpoint = settings.LlmEndpoint,
                Region = settings.LlmRegion,
                SessionToken = settings.LlmSessionToken,
                AwsAccessKeyId = settings.LlmAwsAccessKeyId,
                AwsSecretAccessKey = settings.LlmAwsSecretAccessKey,
                BedrockAuthMode = settings.LlmBedrockAuthMode,
                Model = settings.LlmModel ?? string.Empty,
                FastModel = settings.LlmFastModel,
                Limits = new ProviderLimits
                {
                    MaxConcurrentRequests = settings.LlmMaxConcurrentRequests,
                    TokensPerMinute = settings.LlmTokensPerMinute,
                    RequestsPerMinute = settings.LlmRequestsPerMinute,
                    MonthlyBudget = settings.LlmMonthlyBudget,
                },
            };

            // Atomic publish of the new snapshot — readers via CurrentConfiguration / the
            // factory accessor will see either the old or the new instance, never a torn one.
            Interlocked.Exchange(ref _config, next);

            // The active provider always matches the published configuration: when the new one cannot build a provider,
            // there is no active provider, never the previous one (which could run under other credentials).
            ILlmProvider? rebuilt = null;
            if (!string.IsNullOrEmpty(next.ApiKey) || next.Provider == AiProvider.Bedrock)
            {
                try
                {
                    rebuilt = _factory.CreateProvider();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "LLM provider rebuild after configuration update failed for provider {Provider} ({Failure}). No provider is active until the configuration is fixed.",
                        next.Provider,
                        FailureCodes.Describe(ex));
                }
            }

            Interlocked.Exchange(ref _currentProvider, rebuilt);
        }
    }
}
