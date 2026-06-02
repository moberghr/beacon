using Beacon.Core.Models.Settings;

namespace Beacon.Core.Services;

/// <summary>
/// Allows updating LLM configuration and hot-swapping the provider at runtime.
/// Implemented in Beacon.AI to avoid Core depending on the AI project.
/// </summary>
public interface ILlmConfigurationUpdater
{
    void UpdateConfiguration(AppSettingsData settings);
}
