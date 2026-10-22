using Beacon.AI.Models.Configuration;
using Beacon.Core.Data.Enums;

namespace Beacon.AI.Services.LlmProviders;

internal sealed class ClaudeProviderRegistration : IBuiltInLlmProviderRegistration
{
    private const string DefaultFastModel = "claude-haiku-4-20250514";

    private readonly Func<HttpClient> _httpClient;

    public ClaudeProviderRegistration(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _httpClient = () => httpClientFactory.CreateClient(LlmHttpClients.Claude);
    }

    /// <summary>Builds providers on the shared client, for callers without DI.</summary>
    internal ClaudeProviderRegistration()
    {
        _httpClient = () => LlmHttpClients.Shared;
    }

    public AiProvider Provider => AiProvider.Claude;

    public bool RequiresApiKey => true;

    public ILlmProvider Create(LlmConfiguration configuration, bool fast)
    {
        var model = fast ? BuiltInLlmProviderRegistrations.FastModelOr(configuration, DefaultFastModel) : configuration.Model;

        return new ClaudeProvider(_httpClient(), configuration.ApiKey, model);
    }
}
