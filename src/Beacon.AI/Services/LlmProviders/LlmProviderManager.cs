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
    private ActiveProviders _active = ActiveProviders.None;
    private string? _inactiveReason;

    public LlmProviderManager(
        LlmConfiguration llmConfig,
        LlmProviderFactory factory,
        ILogger<LlmProviderManager> logger)
    {
        _config = llmConfig ?? throw new ArgumentNullException(nameof(llmConfig));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Create initial provider if configuration is valid
        _inactiveReason = NotInstalledReason(_config);
        if (_inactiveReason is not null)
        {
            WarnNotInstalled(_config.Provider);
        }
        else if (HasRequiredApiKey(_config))
        {
            try
            {
                var main = factory.CreateProvider(_config);
                _active = new ActiveProviders(main, BuildFastProvider(_config));
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

    public ILlmProvider? CurrentProvider => Volatile.Read(ref _active).Main;

    /// <summary>
    /// The provider on the configured fast model, for short classification calls; null when no fast model is configured
    /// (or it equals the main model, or it could not be built), in which case the main provider serves those calls.
    /// </summary>
    public ILlmProvider? FastProvider => Volatile.Read(ref _active).Fast;

    /// <summary>
    /// The main and fast providers of one configuration, read together: a caller that needs both reads this once, so a
    /// fast provider is never paired with a main provider built from another configuration.
    /// </summary>
    public (ILlmProvider? Main, ILlmProvider? Fast) ActiveProvidersSnapshot
    {
        get
        {
            var active = Volatile.Read(ref _active);

            return (active.Main, active.Fast);
        }
    }

    /// <summary>
    /// Why there is no active provider when the cause is that the configured provider is not installed: the install
    /// message (provider and package names only, no configuration values). Null otherwise.
    /// </summary>
    public string? InactiveReason => Volatile.Read(ref _inactiveReason);

    /// <summary>
    /// Snapshot of the currently active configuration. Always returns the most recently
    /// installed instance; never mutated in place.
    /// </summary>
    public LlmConfiguration CurrentConfiguration => Volatile.Read(ref _config);

    /// <summary>
    /// The main and fast providers together with the configuration they were built from, read as one unit: a
    /// configuration update running at the same time is seen either whole or not at all.
    /// </summary>
    internal (ILlmProvider? Main, ILlmProvider? Fast, LlmConfiguration Configuration) ActiveSnapshot()
    {
        lock (_lock)
        {
            return (_active.Main, _active.Fast, _config);
        }
    }

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
            var rebuilt = ActiveProviders.None;
            var inactiveReason = NotInstalledReason(next);
            if (inactiveReason is not null)
            {
                // Reported when the provider changes to one that is not installed, not again on every save that keeps it.
                if (next.Provider != previous.Provider)
                {
                    WarnNotInstalled(next.Provider);
                }
            }
            else if (HasRequiredApiKey(next))
            {
                try
                {
                    var main = _factory.CreateProvider(next);
                    rebuilt = new ActiveProviders(main, BuildFastProvider(next));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "LLM provider rebuild after configuration update failed for provider {Provider} ({Failure}). No provider is active until the configuration is fixed.",
                        next.Provider,
                        FailureCodes.Describe(ex));
                }
            }

            Interlocked.Exchange(ref _active, rebuilt);
            Interlocked.Exchange(ref _inactiveReason, inactiveReason);
        }
    }

    // A fast-tier request never fails because the fast model is unavailable: a build failure leaves no fast provider.
    private ILlmProvider? BuildFastProvider(LlmConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.FastModel)
            || string.Equals(config.FastModel.Trim(), config.Model?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return _factory.CreateFastProvider(config);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Fast LLM provider construction failed for provider {Provider} ({Failure}). Fast-tier calls use the main provider.",
                config.Provider,
                FailureCodes.Describe(ex));

            return null;
        }
    }

    /// <summary>The install message when <paramref name="config"/> names a provider that is not installed, else null.</summary>
    private string? NotInstalledReason(LlmConfiguration config)
    {
        return _factory.IsRegistered(config.Provider)
            ? null
            : LlmProviderFactory.NotInstalledMessage(config.Provider);
    }

    private bool HasRequiredApiKey(LlmConfiguration config)
    {
        return !_factory.RequiresApiKey(config.Provider) || !string.IsNullOrEmpty(config.ApiKey);
    }

    private void WarnNotInstalled(AiProvider provider)
    {
        _logger.LogWarning(
            "LLM provider {Provider} is not installed: its package is not referenced or its registration was not added. No provider is active; install the provider or choose another one in Admin Settings.",
            provider);
    }

    private sealed record ActiveProviders(ILlmProvider? Main, ILlmProvider? Fast)
    {
        public static ActiveProviders None { get; } = new(null, null);
    }
}
