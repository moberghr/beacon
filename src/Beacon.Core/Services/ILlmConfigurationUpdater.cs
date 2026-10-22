using Beacon.Core.Models.Settings;

namespace Beacon.Core.Services;

/// <summary>
/// Allows updating LLM configuration and hot-swapping the provider at runtime.
/// Implemented in Beacon.AI to avoid Core depending on the AI project.
/// </summary>
public interface ILlmConfigurationUpdater
{
    void UpdateConfiguration(AppSettingsData settings);

    /// <summary>
    /// Applies only the stored LLM limits (concurrency, requests and tokens per minute, whether those are enforced),
    /// keeping the provider and its credentials; used at startup when limits were stored without a provider. An
    /// implementation that does not override it ignores the stored limits.
    /// </summary>
    void UpdateLimits(AppSettingsData settings)
    {
    }
}
