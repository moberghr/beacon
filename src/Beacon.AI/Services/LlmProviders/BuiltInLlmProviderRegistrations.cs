using Beacon.AI.Models.Configuration;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>The providers <c>Moberg.Beacon.AI</c> ships: OpenAI, Azure OpenAI and Claude.</summary>
internal static class BuiltInLlmProviderRegistrations
{
    public static IReadOnlyList<ILlmProviderRegistration> Create()
    {
        return
        [
            new OpenAiProviderRegistration(),
            new ClaudeProviderRegistration(),
            new AzureOpenAiProviderRegistration(),
        ];
    }

    /// <summary>The configured fast model, or <paramref name="fallback"/> when none is set.</summary>
    public static string FastModelOr(LlmConfiguration configuration, string fallback)
    {
        return string.IsNullOrWhiteSpace(configuration.FastModel) ? fallback : configuration.FastModel;
    }
}
