using Microsoft.Extensions.DependencyInjection;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// The HTTP clients the built-in LLM providers send through: one named <see cref="IHttpClientFactory"/> client per
/// provider when registered through <c>AddBeaconAI</c>, and one shared pooled client for providers built without DI.
/// Long completions are expected, so every client waits up to <see cref="Timeout"/>. No client follows redirects: a
/// redirect would carry the key headers and the request body to wherever the response points.
/// </summary>
internal static class LlmHttpClients
{
    public const string OpenAi = "Beacon.AI.OpenAI";

    public const string AzureOpenAi = "Beacon.AI.AzureOpenAI";

    public const string Claude = "Beacon.AI.Claude";

    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private static readonly Lazy<HttpClient> SharedClient = new(CreateShared);

    /// <summary>
    /// The client for providers built without DI (the obsolete provider constructors and the factory's obsolete
    /// constructor). One instance for the process, so connections are pooled and DNS changes are picked up.
    /// </summary>
    public static HttpClient Shared => SharedClient.Value;

    public static IServiceCollection AddLlmHttpClients(this IServiceCollection services)
    {
        foreach (var name in new[] { OpenAi, AzureOpenAi, Claude })
        {
            services
                .AddHttpClient(name, x => x.Timeout = Timeout)
                .ConfigurePrimaryHttpMessageHandler(CreatePrimaryHandler);
        }

        return services;
    }

    /// <summary>
    /// The handler every LLM client sends through. Connections are recycled after a while so DNS changes are picked up
    /// (the factory also rotates its handlers; the shared client relies on this alone).
    /// </summary>
    internal static SocketsHttpHandler CreatePrimaryHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
    }

    private static HttpClient CreateShared()
    {
        return new HttpClient(CreatePrimaryHandler())
        {
            Timeout = Timeout
        };
    }
}
