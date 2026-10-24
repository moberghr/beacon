using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Api;

/// <summary>
/// The server and outgoing HTTP timeouts Beacon's AI work needs: documenting a data source, an ask or an LLM call can run
/// for minutes, which the framework defaults (a Kestrel keep-alive of 130 seconds, a 30-second request-header timeout and a
/// 100-second <see cref="HttpClient"/> timeout) would cut off.
/// </summary>
public static class BeaconHostTimeouts
{
    /// <summary>Five minutes: the Kestrel keep-alive and request-header timeouts and the default HttpClient timeout.</summary>
    public static TimeSpan LongRunningTimeout { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Sets Kestrel's keep-alive and request-header timeouts and the default timeout of every <see cref="HttpClient"/> the
    /// host's <c>IHttpClientFactory</c> creates to <see cref="LongRunningTimeout"/>. These are host-wide settings, which is
    /// why Beacon never applies them itself: call it when the host runs Beacon's AI features, and lower nothing a
    /// long-running request depends on. A client configured with its own timeout keeps it.
    /// </summary>
    public static WebApplicationBuilder ConfigureBeaconTimeouts(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WebHost.ConfigureKestrel(x =>
        {
            x.Limits.KeepAliveTimeout = LongRunningTimeout;
            x.Limits.RequestHeadersTimeout = LongRunningTimeout;
        });
        builder.Services.AddHttpClient().ConfigureHttpClientDefaults(x => x.ConfigureHttpClient(y => y.Timeout = LongRunningTimeout));

        return builder;
    }
}
