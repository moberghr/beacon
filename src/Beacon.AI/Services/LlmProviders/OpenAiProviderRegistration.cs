using Beacon.AI.Models.Configuration;
using Beacon.Core.Data.Enums;

namespace Beacon.AI.Services.LlmProviders;

internal sealed class OpenAiProviderRegistration : IBuiltInLlmProviderRegistration
{
    private const string DefaultFastModel = "gpt-4o-mini";

    private readonly Func<HttpClient> _httpClient;

    public OpenAiProviderRegistration(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _httpClient = () => httpClientFactory.CreateClient(LlmHttpClients.OpenAi);
    }

    /// <summary>Builds providers on the shared client, for callers without DI.</summary>
    internal OpenAiProviderRegistration()
    {
        _httpClient = () => LlmHttpClients.Shared;
    }

    public AiProvider Provider => AiProvider.OpenAI;

    public bool RequiresApiKey => true;

    public ILlmProvider Create(LlmConfiguration configuration, bool fast)
    {
        var model = fast ? BuiltInLlmProviderRegistrations.FastModelOr(configuration, DefaultFastModel) : configuration.Model;

        return OpenAiCompatibleProvider.ForOpenAi(_httpClient(), configuration.ApiKey, model);
    }
}
