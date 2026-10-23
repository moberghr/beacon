using System.Diagnostics;
using System.Globalization;
using System.Threading.RateLimiting;
using Beacon.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Api.Endpoints;

internal static class HealthEndpoints
{
    private const string RoutePath = "/health";

    /// <summary>
    /// The health route as requested by load-balancer probes. It must answer before first-run setup
    /// completes, so <see cref="Authentication.FirstRunSetupMiddleware"/> lets exactly this path through.
    /// </summary>
    public const string RequestPath = BeaconApiEndpoints.GroupPrefix + RoutePath;

    public static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet(RoutePath, async (HttpContext http, IDbContextFactory<BeaconContext> contextFactory, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            {
                if (HealthProbeCache.TryGetFresh(out var cachedHealthy))
                {
                    return HealthResult(http, cachedHealthy);
                }

                // One probe at a time: callers arriving while one runs wait for it and share its outcome.
                await HealthProbeCache.Gate.WaitAsync(cancellationToken);
                try
                {
                    if (HealthProbeCache.TryGetFresh(out cachedHealthy))
                    {
                        return HealthResult(http, cachedHealthy);
                    }

                    // The throttle guards the database probe only; a cached answer is never refused.
                    if (!HealthRateLimiter.TryAcquire(http, out var retryAfterSeconds))
                    {
                        http.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

                        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
                    }

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                    try
                    {
                        await using var context = await contextFactory.CreateDbContextAsync(timeoutCts.Token);
                        var canConnect = await context.Database.CanConnectAsync(timeoutCts.Token);
                        HealthProbeCache.Store(canConnect);

                        return HealthResult(http, canConnect);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Log server-side so a flapping readiness probe has a recorded cause; the response
                        // itself stays generic and must not leak connection details.
                        loggerFactory.CreateLogger("Beacon.Health").LogError(ex, "Health check database connectivity failed.");
                    }

                    HealthProbeCache.Store(false);

                    return HealthResult(http, false);
                }
                finally
                {
                    HealthProbeCache.Gate.Release();
                }
            })
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithName("Health")
            .WithTags("Health");

        return group;
    }

    // Anonymous callers (load-balancer probes) learn only ok or unavailable; the cause of a failure is for signed-in callers.
    private static IResult HealthResult(HttpContext http, bool healthy)
    {
        if (healthy)
        {
            return Results.Ok(new HealthResponse("ok"));
        }

        return Results.Problem(
            title: "Service Unavailable",
            detail: http.User.Identity?.IsAuthenticated == true ? "Database connectivity check failed." : null,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

/// <summary>
/// The database probe's outcome is shared for a couple of seconds, so a flood of anonymous requests costs one probe,
/// not one connection each.
/// </summary>
internal static class HealthProbeCache
{
    private static long _ttlTicks = Stopwatch.Frequency * 2;
    public static readonly SemaphoreSlim Gate = new(1, 1);
    private static ProbeOutcome? _last;

    public static bool TryGetFresh(out bool healthy)
    {
        var last = Volatile.Read(ref _last);
        healthy = last?.Healthy ?? false;

        return last != null && Stopwatch.GetTimestamp() - last.Ticks < Volatile.Read(ref _ttlTicks);
    }

    public static void Store(bool healthy)
    {
        Volatile.Write(ref _last, new ProbeOutcome(Stopwatch.GetTimestamp(), healthy));
    }

    internal static void Reset()
    {
        Volatile.Write(ref _last, null);
        Volatile.Write(ref _ttlTicks, Stopwatch.Frequency * 2);
    }

    internal static void SetTtl(TimeSpan ttl)
    {
        Volatile.Write(ref _ttlTicks, (long)(ttl.TotalSeconds * Stopwatch.Frequency));
    }

    private sealed record ProbeOutcome(long Ticks, bool Healthy);
}

/// <summary>
/// A per-IP fixed window on the database probe of the anonymous health route: 120 probes per minute. It is consulted only
/// when a probe would actually run, so a cached answer (what a flood and the load-balancer probe both get) is never refused.
/// Self-contained on purpose (no <c>AddRateLimiter</c>), like the login throttle.
/// </summary>
internal static class HealthRateLimiter
{
    private const int PermitLimit = 120;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private static readonly PartitionedRateLimiter<string> Limiter = PartitionedRateLimiter.Create<string, string>(
        x =>
            RateLimitPartition.GetFixedWindowLimiter(
                x,
                _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = PermitLimit,
                        Window = Window,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));

    public static bool TryAcquire(HttpContext httpContext, out int retryAfterSeconds)
    {
        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        using var lease = Limiter.AttemptAcquire(remoteIp);
        retryAfterSeconds = 0;
        if (lease.IsAcquired)
        {
            return true;
        }

        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : Window;
        retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));

        return false;
    }
}

internal sealed record HealthResponse(string Status);
