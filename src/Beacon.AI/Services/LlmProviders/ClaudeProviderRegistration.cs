using Beacon.AI.Models.Configuration;
using Beacon.Core.Data.Enums;

namespace Beacon.AI.Services.LlmProviders;

internal sealed class ClaudeProviderRegistration : ILlmProviderRegistration
{
    private const string DefaultFastModel = "claude-haiku-4-20250514";

    public AiProvider Provider => AiProvider.Claude;

    public bool RequiresApiKey => true;

    public ILlmProvider Create(LlmConfiguration configuration, bool fast)
    {
        var model = fast ? BuiltInLlmProviderRegistrations.FastModelOr(configuration, DefaultFastModel) : configuration.Model;

        return new ClaudeProvider(configuration.ApiKey, model);
    }
}
