using Beacon.AI.Models.Configuration;
using Beacon.Core.Data.Enums;
using Microsoft.Extensions.Logging;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// Builds concrete <see cref="ILlmProvider"/> instances from the current
/// <see cref="LlmConfiguration"/> snapshot returned by the supplied accessor.
/// Each call reads the latest configuration so hot-swaps pick up new values.
/// A provider is built only through its <see cref="ILlmProviderRegistration"/>. A registration that
/// <c>Moberg.Beacon.AI</c> ships (OpenAI, Azure OpenAI, Claude) is used only when no other registration
/// claims its provider, wherever <c>AddBeaconAI</c> was called; when two other registrations claim the
/// same provider, the one registered last is used and a warning is logged.
/// </summary>
public class LlmProviderFactory
{
    private readonly Func<LlmConfiguration> _configAccessor;
    private readonly Dictionary<AiProvider, ILlmProviderRegistration> _registrations = [];

    public LlmProviderFactory(
        Func<LlmConfiguration> configAccessor,
        IEnumerable<ILlmProviderRegistration> registrations,
        ILogger<LlmProviderFactory>? logger = null)
    {
        _configAccessor = configAccessor ?? throw new ArgumentNullException(nameof(configAccessor));
        ArgumentNullException.ThrowIfNull(registrations);

        // The built-ins go first, so any other registration for the same provider replaces them (stable order).
        var ordered = registrations
            .OrderBy(x => IsBuiltIn(x) ? 0 : 1);
        foreach (var registration in ordered)
        {
            if (_registrations.TryGetValue(registration.Provider, out var existing)
                && IsBuiltIn(existing) == IsBuiltIn(registration))
            {
                logger?.LogWarning(
                    "More than one registration claims LLM provider {Provider}; the one registered last is used.",
                    registration.Provider);
            }

            _registrations[registration.Provider] = registration;
        }

        RegisteredProviders = _registrations.Keys
            .Order()
            .ToList();
    }

    [Obsolete("Pass the provider registrations (resolve IEnumerable<ILlmProviderRegistration> from DI). This constructor knows only the providers Moberg.Beacon.AI ships (OpenAI, Azure OpenAI, Claude) and cannot build Bedrock, which now lives in Moberg.Beacon.AI.Bedrock.")]
    public LlmProviderFactory(Func<LlmConfiguration> configAccessor, ILogger<LlmProviderFactory>? logger = null)
        : this(configAccessor, BuiltInLlmProviderRegistrations.Create(), logger)
    {
    }

    /// <summary>The installed providers, in <see cref="AiProvider"/> order.</summary>
    public IReadOnlyList<AiProvider> RegisteredProviders { get; }

    public bool IsRegistered(AiProvider provider)
    {
        return _registrations.ContainsKey(provider);
    }

    /// <summary>
    /// Whether <paramref name="provider"/> needs an API key. True for a provider that is not installed, so a caller that
    /// activates a provider only once its key is set never tries to activate it.
    /// </summary>
    public bool RequiresApiKey(AiProvider provider)
    {
        return !_registrations.TryGetValue(provider, out var registration) || registration.RequiresApiKey;
    }

    public ILlmProvider CreateProvider()
    {
        var config = _configAccessor();

        return RegistrationFor(config.Provider).Create(config, fast: false);
    }

    public ILlmProvider CreateFastProvider()
    {
        var config = _configAccessor();

        return RegistrationFor(config.Provider).Create(config, fast: true);
    }

    /// <summary>
    /// Builds the main provider for <paramref name="configuration"/> without reading the accessor: the manager builds from
    /// the snapshot it holds, since the DI accessor resolves the manager itself and would re-enter its construction.
    /// </summary>
    internal ILlmProvider CreateProvider(LlmConfiguration configuration)
    {
        return RegistrationFor(configuration.Provider).Create(configuration, fast: false);
    }

    // Beacon.Core's LlmProviderInstallation.NotInstalledMessage (the 400 for an uninstalled pick) words it the same:
    // Core cannot reference this assembly, so a test pins the two equal.
    internal static string NotInstalledMessage(AiProvider provider)
    {
        return $"LLM provider {provider} is not installed: reference Moberg.Beacon.AI.{provider} and call AddBeacon{provider}().";
    }

    private static bool IsBuiltIn(ILlmProviderRegistration registration)
    {
        return registration is IBuiltInLlmProviderRegistration;
    }

    private ILlmProviderRegistration RegistrationFor(AiProvider provider)
    {
        return _registrations.TryGetValue(provider, out var registration)
            ? registration
            : throw new InvalidOperationException(NotInstalledMessage(provider));
    }
}
