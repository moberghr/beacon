using Beacon.Core.Exceptions;
using Beacon.Core.Http;
using Beacon.Core.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// The HTTP clients the built-in LLM providers send through: one named <see cref="IHttpClientFactory"/> client per
/// provider when registered through <c>AddBeaconAI</c>, and one shared pooled client for providers built without DI.
/// Long completions are expected, so every client waits up to <see cref="Timeout"/>. No client follows redirects: a
/// redirect would carry the key headers and the request body to wherever the response points. Every client connects only
/// to an address the LLM address rules allow (<see cref="OutboundAddressRules.Llm"/>: loopback and private networks for
/// local and on-premises gateways, never link-local or cloud metadata), judged on the addresses the host resolves to when
/// it connects; a refusal is an <see cref="AiServiceException"/> naming the rule, never the address or endpoint. The named
/// clients log no requests: a request URI can name an Azure resource, region and deployment.
/// </summary>
internal static class LlmHttpClients
{
    public const string OpenAi = "Beacon.AI.OpenAI";

    public const string AzureOpenAi = "Beacon.AI.AzureOpenAI";

    public const string Claude = "Beacon.AI.Claude";

    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>The largest successful response body a provider reads (8 MiB); a larger one fails the call.</summary>
    internal const long MaxResponseBytes = 8 * 1024 * 1024;

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
                .ConfigurePrimaryHttpMessageHandler(x => CreatePrimaryHandler(Guard(x.GetService<IHostAddressResolver>())))
                .AddHttpMessageHandler(x => CheckHandler(Guard(x.GetService<IHostAddressResolver>())))
                .RemoveAllLoggers();
        }

        return services;
    }

    /// <summary>
    /// The handler every LLM client sends through. Connections are recycled after a while so DNS changes are picked up
    /// (the factory also rotates its handlers; the shared client relies on this alone).
    /// </summary>
    internal static SocketsHttpHandler CreatePrimaryHandler()
    {
        return CreatePrimaryHandler(Guard(null));
    }

    internal static SocketsHttpHandler CreatePrimaryHandler(OutboundAddressGuard guard)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = guard.ConnectAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
    }

    internal static OutboundAddressCheckHandler CheckHandler(OutboundAddressGuard guard)
    {
        return new OutboundAddressCheckHandler(guard, x => new AiServiceException(RefusalMessage(x)));
    }

    internal static string RefusalMessage(OutboundAddressRefusal refusal)
    {
        return refusal == OutboundAddressRefusal.LinkLocal
            ? "The LLM endpoint resolves to a link-local or cloud metadata address, which LLM calls never reach."
            : "The LLM endpoint resolves to an unspecified, multicast or reserved address, which LLM calls never reach.";
    }

    /// <summary>
    /// Reads <paramref name="content"/> up to <paramref name="maxBytes"/>: null when the declared length or the bytes read
    /// pass it, so a body is never buffered past the cap. The response must have been read with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, or the client has already buffered it whole.
    /// </summary>
    internal static async Task<byte[]?> ReadCappedAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static OutboundAddressGuard Guard(IHostAddressResolver? resolver)
    {
        return new OutboundAddressGuard(OutboundAddressRules.Llm, resolver ?? new DnsHostAddressResolver());
    }

    private static HttpClient CreateShared()
    {
        var guard = Guard(null);
        var checkHandler = CheckHandler(guard);
        checkHandler.InnerHandler = CreatePrimaryHandler(guard);

        return new HttpClient(checkHandler)
        {
            Timeout = Timeout
        };
    }
}
