using Beacon.Core.Data.Enums;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.AdminSettings;

/// <summary>
/// Refuses (400) a request that picks an LLM provider this host has not installed, so it is neither stored nor tested.
/// A request that keeps the stored provider passes even when that one is not installed (the other settings can still
/// be saved), and nothing is refused while AI is off (settings can be prepared before AI is turned on).
/// </summary>
internal static class LlmProviderInstallation
{
    public static void DemandInstalled(ILlmProviderCatalog catalog, AiProvider? requested, AiProvider? stored)
    {
        if (!catalog.AiEnabled || requested is not { } provider || provider == stored)
        {
            return;
        }

        if (catalog.Registered.Any(x => x.Provider == provider))
        {
            return;
        }

        throw new InvalidOperationException(NotInstalledMessage(provider));
    }

    // Beacon.AI's LlmProviderFactory.NotInstalledMessage (what the factory, the manager and the connection test report)
    // words it the same: Core cannot reference Beacon.AI, so a test pins the two equal.
    public static string NotInstalledMessage(AiProvider provider)
    {
        return $"LLM provider {provider} is not installed: reference Moberg.Beacon.AI.{provider} and call AddBeacon{provider}().";
    }
}
