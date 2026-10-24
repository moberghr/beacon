using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Services.Security;
using Beacon.Shared.Http;

namespace Beacon.Api.Authentication;

/// <summary>
/// Self-contained login throttle: <c>Beacon:RateLimits:Login</c> attempts (default 10 per 60 seconds) per remote IP. It
/// does not depend on <c>AddRateLimiter</c> / <c>UseRateLimiter</c>, so every host that maps the login endpoints is
/// throttled and a host's own rate-limiter configuration (with or without a "login" policy) cannot break login. Behind a
/// reverse proxy the host must run <c>UseForwardedHeaders</c> with known proxies, otherwise every user shares the proxy's
/// partition.
/// </summary>
internal sealed class LoginRateLimiter : IDisposable
{
    private readonly FixedWindowPartitions _limiter;

    public LoginRateLimiter()
        : this(Options.Create(new RateLimitOptions()))
    {
    }

    public LoginRateLimiter(IOptions<RateLimitOptions> options)
    {
        _limiter = new FixedWindowPartitions(options.Value.Login, options.Value.MaxPartitions);
    }

    /// <summary>
    /// The registered limiter, or for hosts that call <c>MapBeaconApi()</c> without <c>AddBeaconApiServices()</c> (where
    /// none is registered) one stand-in per application built from the <c>Beacon:RateLimits</c> options the host registered,
    /// or the defaults.
    /// </summary>
    internal static LoginRateLimiter For(IServiceProvider? services)
    {
        return services?.GetService<LoginRateLimiter>()
            ?? UnregisteredLimiter<LoginRateLimiter>.For(services, x => new LoginRateLimiter(x));
    }

    public bool TryAcquire(string partitionKey, out TimeSpan retryAfter)
    {
        return _limiter.TryAcquire(partitionKey, out retryAfter);
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
        var limiter = LoginRateLimiter.For(httpContext.RequestServices);
        var partition = partitionPrefix + RateLimitPartitions.OfAddress(httpContext.Connection.RemoteIpAddress);

        if (limiter.TryAcquire(partition, out var retryAfter))
        {
            return await next(context);
        }

        return RateLimitRefusal.Result(httpContext, retryAfter);
    }
}

/// <summary>
/// The answer to a request over one of Beacon's request limits: 429 as <c>application/problem+json</c> with a
/// <c>Retry-After</c> header in whole seconds.
/// </summary>
internal static class RateLimitRefusal
{
    public const string Title = "Too Many Requests";

    public static IResult Result(HttpContext context, TimeSpan retryAfter)
    {
        var seconds = RateLimitPartitions.RetryAfterSeconds(retryAfter);
        context.Response.Headers.RetryAfter = seconds;

        return Results.Problem(
            title: Title,
            detail: $"Too many requests; retry in {seconds} seconds.",
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    public static Task WriteAsync(HttpContext context, TimeSpan retryAfter)
    {
        return Result(context, retryAfter).ExecuteAsync(context);
    }
}
