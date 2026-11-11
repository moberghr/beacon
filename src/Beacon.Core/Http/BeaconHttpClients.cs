using Beacon.Core.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Beacon.Core.Http;

/// <summary>
/// The named <see cref="IHttpClientFactory"/> clients for Beacon's outbound calls that carry credentials or read
/// third-party documents: the JWKS fetch, Databricks and GitHub. None follows redirects (a redirect would carry the
/// credential header to wherever the response points), none keeps cookies, and each has its own timeout. The JWKS and
/// Databricks clients also cap the response body, and the Databricks client gives up connecting after
/// <see cref="DatabricksConnectTimeout"/> and connects only to an address the data-source address rules allow (<see cref="DataSourceOutboundAddresses"/>), judged on the addresses the host resolves to when it
/// connects. The Databricks client's request logging is removed: its request URIs name the workspace host, warehouse paths
/// and statement ids.
/// </summary>
public static class BeaconHttpClients
{
    public const string Jwks = "Beacon.Jwks";

    public const string Databricks = "Beacon.Databricks";

    public const string GitHub = "GitHub";

    public const long JwksMaxResponseBytes = 256 * 1024;

    public const long DatabricksMaxResponseBytes = 32 * 1024 * 1024;

    public static readonly TimeSpan JwksTimeout = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan GitHubTimeout = TimeSpan.FromSeconds(60);

    // A Databricks call sets its own timeout from the data source's query timeout; this is the ceiling for reading a body.
    private static readonly TimeSpan DatabricksReadTimeout = TimeSpan.FromMinutes(16);

    /// <summary>
    /// How long the Databricks client tries to connect (every address the host resolves to, in turn) before it gives up,
    /// as the REST API connector's client does.
    /// </summary>
    internal static readonly TimeSpan DatabricksConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Registers the named clients. Safe to call more than once.</summary>
    public static IServiceCollection AddBeaconOutboundHttpClients(this IServiceCollection services)
    {
        if (services.Any(x => x.ServiceType == typeof(OutboundClientsMarker)))
        {
            return services;
        }

        services.TryAddSingleton<OutboundClientsMarker>();

        services
            .AddHttpClient(Jwks)
            .ConfigureHttpClient(x => x.Timeout = JwksTimeout)
            .ConfigurePrimaryHttpMessageHandler(CreatePrimaryHandler)
            .AddHttpMessageHandler(() => new RedirectRefusalHandler())
            .AddHttpMessageHandler(() => new ResponseSizeLimitHandler(JwksMaxResponseBytes, JwksTimeout));

        services
            .AddHttpClient(Databricks)
            .ConfigurePrimaryHttpMessageHandler(x => CreateDataSourceHandler(DataSourceOutboundAddresses.Guard(x)))
            .AddHttpMessageHandler(x => DataSourceOutboundAddresses.CheckHandler(DataSourceOutboundAddresses.Guard(x)))
            .AddHttpMessageHandler(() => new ResponseSizeLimitHandler(DatabricksMaxResponseBytes, DatabricksReadTimeout))
            .RemoveAllLoggers();

        // GitHub reads are bounded per call (tree, file) by the scanner's own limits.
        services
            .AddHttpClient(GitHub)
            .ConfigureHttpClient(x => x.Timeout = GitHubTimeout)
            .ConfigurePrimaryHttpMessageHandler(CreatePrimaryHandler)
            .AddHttpMessageHandler(() => new RedirectRefusalHandler());

        return services;
    }

    internal static SocketsHttpHandler CreatePrimaryHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
    }

    internal static SocketsHttpHandler CreateDataSourceHandler(OutboundAddressGuard guard)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = DatabricksConnectTimeout,
            ConnectCallback = guard.ConnectAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
    }

    private sealed class OutboundClientsMarker;
}
