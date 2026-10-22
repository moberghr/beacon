using Beacon.AI.Models.Configuration;
using Beacon.Core.Data.Enums;

namespace Beacon.AI.Services.LlmProviders;

internal sealed class AzureOpenAiProviderRegistration : IBuiltInLlmProviderRegistration
{
    private readonly Func<HttpClient> _httpClient;

    public AzureOpenAiProviderRegistration(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _httpClient = () => httpClientFactory.CreateClient(LlmHttpClients.AzureOpenAi);
    }

    /// <summary>Builds providers on the shared client, for callers without DI.</summary>
    internal AzureOpenAiProviderRegistration()
    {
        _httpClient = () => LlmHttpClients.Shared;
    }

    public AiProvider Provider => AiProvider.AzureOpenAI;

    public bool RequiresApiKey => true;

    public ILlmProvider Create(LlmConfiguration configuration, bool fast)
    {
        if (string.IsNullOrEmpty(configuration.Endpoint))
        {
            throw new InvalidOperationException("Azure OpenAI endpoint is required");
        }

        // The fast model falls back to the main deployment: Azure has no fixed fast model name.
        var model = fast ? BuiltInLlmProviderRegistrations.FastModelOr(configuration, configuration.Model) : configuration.Model;

        return OpenAiCompatibleProvider.ForAzure(_httpClient(), configuration.Endpoint, configuration.ApiKey, model);
    }
}
