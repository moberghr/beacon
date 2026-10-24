using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Connector.Api.Services;

/// <summary>
/// The HTTP client of the REST API connector: its queries and its connection test. Its handler never follows redirects
/// (a redirect would carry the data source's header credentials and the caller's headers to another address), keeps no
/// cookies and gives up connecting after <see cref="ConnectTimeout"/>; the connector bounds each request's whole wait and
/// the size of the body it reads. The client's request logging is removed, because a request's path and query can carry
/// identifiers or a credential, and every request is marked so a host's HTTP tracing can leave it out
/// (<see cref="ApiConnectorRequests.IsApiConnectorRequest"/>).
/// </summary>
public static class ApiConnectorHttpClient
{
    public const string Name = "Beacon.ApiConnector";

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    internal static IServiceCollection AddApiConnectorHttpClient(this IServiceCollection services)
    {
        services
            .AddHttpClient(Name)
            .ConfigurePrimaryHttpMessageHandler(() => CreatePrimaryHandler())
            .AddHttpMessageHandler(() => new ApiConnectorRequestMarker())
            .RemoveAllLoggers();

        return services;
    }

    internal static SocketsHttpHandler CreatePrimaryHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = ConnectTimeout,
            // Re-resolve hosts regularly instead of pinning a connection forever.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
    }
}

/// <summary>
/// Identifies requests made by the REST API connector. Their URLs can carry identifiers or a credential, so a host that
/// traces outgoing HTTP should leave them out, for example with OpenTelemetry's
/// <c>AddHttpClientInstrumentation(x =&gt; x.FilterHttpRequestMessage = y =&gt; !ApiConnectorRequests.IsApiConnectorRequest(y))</c>.
/// </summary>
public static class ApiConnectorRequests
{
    internal static readonly HttpRequestOptionsKey<bool> Marker = new("Beacon.ApiConnectorRequest");

    public static bool IsApiConnectorRequest(HttpRequestMessage request)
    {
        return request.Options.TryGetValue(Marker, out var marked) && marked;
    }
}

/// <summary>Runs first on every connector request: marks it for tracing filters, before the handler that starts the activity.</summary>
internal sealed class ApiConnectorRequestMarker : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Options.Set(ApiConnectorRequests.Marker, true);
        return base.SendAsync(request, cancellationToken);
    }
}
