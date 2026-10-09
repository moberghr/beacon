using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Beacon.Api.Authentication;

/// <summary>
/// Self-contained login throttle: 10 attempts per 60 seconds per remote IP. It does not depend on
/// <c>AddRateLimiter</c> / <c>UseRateLimiter</c>, so every host that maps the login endpoints is
/// throttled and a host's own rate-limiter configuration (with or without a "login" policy) cannot
/// break login. Behind a reverse proxy the host must run <c>UseForwardedHeaders</c> with known
/// proxies, otherwise every user shares the proxy's partition.
/// </summary>
internal sealed class LoginRateLimiter : IDisposable
{
    private const int PermitLimit = 10;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private static readonly Lazy<LoginRateLimiter> DefaultInstance = new(() => new LoginRateLimiter());

    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(
        x =>
            RateLimitPartition.GetFixedWindowLimiter(
                x,
                _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = PermitLimit,
                        Window = Window,
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    }));

    // Exists for hosts that call MapBeaconApi() without AddBeaconApiServices(), where no LoginRateLimiter is registered.
    /// <summary>Process-wide fallback used when the host never called <c>AddBeaconApiServices</c>.</summary>
    internal static LoginRateLimiter Default => DefaultInstance.Value;

    public bool TryAcquire(string partitionKey, out TimeSpan retryAfter)
    {
        using var lease = _limiter.AttemptAcquire(partitionKey);
        if (lease.IsAcquired)
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }

        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : Window;
        return false;
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>Endpoint filter that rejects login attempts over the per-IP limit with 429 and <c>Retry-After</c>.</summary>
internal sealed class LoginRateLimitFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        return PerIpThrottle.InvokeAsync(context, next, partitionPrefix: string.Empty);
    }
}

/// <summary>
/// Endpoint filter for the anonymous first-run setup endpoints: the same per-IP limit as login, in its own partition so
/// setup requests never consume an address's login budget (or the reverse).
/// </summary>
internal sealed class SetupRateLimitFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        return PerIpThrottle.InvokeAsync(context, next, partitionPrefix: "setup:");
    }
}

internal static class PerIpThrottle
{
    public static async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        string partitionPrefix)
    {
        var httpContext = context.HttpContext;
        var limiter = httpContext.RequestServices.GetService<LoginRateLimiter>() ?? LoginRateLimiter.Default;
        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (limiter.TryAcquire(partitionPrefix + remoteIp, out var retryAfter))
        {
            return await next(context);
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }
}
