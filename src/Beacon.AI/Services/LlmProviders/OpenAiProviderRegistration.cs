using Beacon.AI.Models.Configuration;
using Beacon.Core.Data.Enums;

namespace Beacon.AI.Services.LlmProviders;

internal sealed class OpenAiProviderRegistration : ILlmProviderRegistration
{
    private const string DefaultFastModel = "gpt-4o-mini";

    public AiProvider Provider => AiProvider.OpenAI;

    public bool RequiresApiKey => true;

    public ILlmProvider Create(LlmConfiguration configuration, bool fast)
    {
        var model = fast ? BuiltInLlmProviderRegistrations.FastModelOr(configuration, DefaultFastModel) : configuration.Model;

        return new OpenAiProvider(configuration.ApiKey, model);
    }
}
