using Beacon.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Api.Endpoints;

internal static class HealthEndpoints
{
    public static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/health", async (IDbContextFactory<BeaconContext> contextFactory, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                try
                {
                    await using var context = await contextFactory.CreateDbContextAsync(timeoutCts.Token);
                    var canConnect = await context.Database.CanConnectAsync(timeoutCts.Token);
                    if (canConnect)
                    {
                        return Results.Ok(new HealthResponse("ok"));
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Log server-side so a flapping readiness probe has a recorded cause; the response
                    // itself stays generic and must not leak connection details.
                    loggerFactory.CreateLogger("Beacon.Health").LogError(ex, "Health check database connectivity failed.");
                }

                return Results.Problem(
                    title: "Service Unavailable",
                    detail: "Database connectivity check failed.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            })
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithName("Health")
            .WithTags("Health");

        return group;
    }
}

internal sealed record HealthResponse(string Status);
