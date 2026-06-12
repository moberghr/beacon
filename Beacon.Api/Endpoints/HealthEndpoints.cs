using Beacon.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Api.Endpoints;

internal static class HealthEndpoints
{
    public static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/health", async (IDbContextFactory<BeaconContext> contextFactory, CancellationToken cancellationToken) =>
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
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Fall through to 503 — readiness probe must not leak connection details.
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
