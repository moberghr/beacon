using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Services.Security;
using Beacon.Shared.Http;

namespace Beacon.MCP.Services;

/// <summary>
/// The MCP request limits of <c>Beacon:RateLimits</c>: <see cref="RateLimitOptions.Mcp"/> per API key or mapped MCP caller
/// on every request to the MCP route (<see cref="McpRateLimitFilter"/>), and <see cref="RateLimitOptions.Ask"/> per caller
/// on the <c>ask</c> tool, through MCP and the playground alike. Kept in process memory, so each limit holds per Beacon
/// instance. A singleton, so every request on the instance shares the counts; registered by <c>AddBeaconMcp</c>.
/// </summary>
internal sealed class McpRateLimiter : IDisposable
{
    /// <summary>The <c>ask</c> tool's answer to a caller over its limit (an audited tool error).</summary>
    public const string AskRefusalFormat = "Too many questions for this credential; retry in {0} seconds.";

    public McpRateLimiter()
        : this(Options.Create(new RateLimitOptions()))
    {
    }

    public McpRateLimiter(IOptions<RateLimitOptions> options)
    {
        Requests = new FixedWindowPartitions(options.Value.Mcp);
        Ask = new FixedWindowPartitions(options.Value.Ask);
    }

    public FixedWindowPartitions Requests { get; }

    public FixedWindowPartitions Ask { get; }

    public void Dispose()
    {
        Requests.Dispose();
        Ask.Dispose();
    }
}

/// <summary>
/// Filter of the MCP route's endpoints: every request takes a permit of <see cref="RateLimitOptions.Mcp"/> in its
/// caller's partition (the API key, the mapped MCP caller). Over the limit it is answered 429 as
/// <c>application/problem+json</c> with <c>Retry-After</c>, before the MCP server reads it.
/// </summary>
internal sealed class McpRateLimitFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var limiter = httpContext.RequestServices.GetService<McpRateLimiter>();
        var partition = RateLimitPartitions.Of(httpContext.User, httpContext.Connection.RemoteIpAddress);
        if (limiter == null || limiter.Requests.TryAcquire(partition, out var retryAfter))
        {
            return await next(context);
        }

        var seconds = RateLimitPartitions.RetryAfterSeconds(retryAfter);
        httpContext.Response.Headers.RetryAfter = seconds;

        return Results.Problem(
            title: "Too Many Requests",
            detail: $"Too many requests; retry in {seconds} seconds.",
            statusCode: StatusCodes.Status429TooManyRequests);
    }
}
