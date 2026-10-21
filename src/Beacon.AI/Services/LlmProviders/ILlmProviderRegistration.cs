using Beacon.AI.Models.Configuration;
using Beacon.Core.Data.Enums;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// One installed LLM provider. <see cref="LlmProviderFactory"/> builds providers only for the registrations it is given:
/// <c>AddBeaconAI</c> registers OpenAI, Azure OpenAI and Claude, and a provider package (such as
/// <c>Moberg.Beacon.AI.Bedrock</c> through <c>AddBeaconBedrock()</c>) adds its own.
/// </summary>
public interface ILlmProviderRegistration
{
    AiProvider Provider { get; }

    /// <summary>Whether the provider needs the API key setting; without one it is never activated.</summary>
    bool RequiresApiKey { get; }

    /// <summary>Builds the provider for <paramref name="configuration"/>; <paramref name="fast"/> selects the fast model.</summary>
    ILlmProvider Create(LlmConfiguration configuration, bool fast);
}
