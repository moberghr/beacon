using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Services.Security;
using Beacon.Shared.Http;

namespace Beacon.Api.Authentication;

/// <summary>
/// The REST request limits of <c>Beacon:RateLimits</c>: <see cref="RateLimitOptions.Api"/> per caller on every
/// <c>/beacon/api</c> request (<see cref="BeaconApiRateLimitFilter"/>), and <see cref="RateLimitOptions.Anonymous"/> per
/// client address on the anonymous requests Beacon answers 401 (<see cref="AnonymousRefusalThrottle"/>). Kept in process
/// memory, so each limit holds per Beacon instance. A singleton, so every request on the instance shares the counts;
/// registered by <c>AddBeaconApiServices</c> and <c>AddBeaconApiAuthorization</c>. A host that registered neither is still
/// limited, by one stand-in per application built from the options it registered (or the defaults).
/// </summary>
internal sealed class BeaconApiRateLimiter : IDisposable
{
    public BeaconApiRateLimiter()
        : this(Options.Create(new RateLimitOptions()))
    {
    }

    public BeaconApiRateLimiter(IOptions<RateLimitOptions> options)
    {
        Api = new FixedWindowPartitions(options.Value.Api, options.Value.MaxPartitions);
        Anonymous = new FixedWindowPartitions(options.Value.Anonymous, options.Value.MaxPartitions);
    }

    public FixedWindowPartitions Api { get; }

    public FixedWindowPartitions Anonymous { get; }

    /// <summary>The registered limiter, or the application's stand-in when none is registered: the limits never fail open.</summary>
    internal static BeaconApiRateLimiter For(HttpContext context)
    {
        var services = context.RequestServices;

        return services?.GetService<BeaconApiRateLimiter>()
            ?? UnregisteredLimiter<BeaconApiRateLimiter>.For(services, x => new BeaconApiRateLimiter(x));
    }

    public void Dispose()
    {
        Api.Dispose();
        Anonymous.Dispose();
    }
}

/// <summary>
/// Group filter of <c>/beacon/api</c>: every request takes a permit of <see cref="RateLimitOptions.Api"/> in its caller's
/// partition (an API key, a mapped MCP caller, a signed-in user; an anonymous request by client address). Over the limit
/// the request is answered 429 with <c>Retry-After</c> before any handler runs.
/// </summary>
internal sealed class BeaconApiRateLimitFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var partition = RateLimitPartitions.Of(httpContext.User, httpContext.Connection.RemoteIpAddress);
        var limiter = BeaconApiRateLimiter.For(httpContext);
        if (limiter.Api.TryAcquire(partition, out var retryAfter))
        {
            return await next(context);
        }

        return RateLimitRefusal.Result(httpContext, retryAfter);
    }
}

/// <summary>
/// The anonymous requests Beacon refuses with 401 on its own routes (no credential, an unknown or expired API key or
/// token) take a permit of <see cref="RateLimitOptions.Anonymous"/> per client address; over the limit they are answered
/// 429 with <c>Retry-After</c> instead. Requests outside Beacon's routes (<see cref="BeaconRequestPaths"/>) are never
/// counted.
/// </summary>
internal static class AnonymousRefusalThrottle
{
    /// <summary>True when the 401 may be sent; false when the caller's address is over the limit.</summary>
    public static bool TryAdmit(HttpContext context, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!BeaconRequestPaths.IsBeaconRequest(context))
        {
            return true;
        }

        return BeaconApiRateLimiter.For(context).Anonymous.TryAcquire(RateLimitPartitions.OfAddress(context.Connection.RemoteIpAddress), out retryAfter);
    }
}
