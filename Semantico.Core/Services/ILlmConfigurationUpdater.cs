using Semantico.Core.Models.Settings;

namespace Semantico.Core.Services;

/// <summary>
/// Allows updating LLM configuration and hot-swapping the provider at runtime.
/// Implemented in Semantico.AI to avoid Core depending on the AI project.
/// </summary>
public interface ILlmConfigurationUpdater
{
    void UpdateConfiguration(AppSettingsData settings);
}
