namespace Beacon.Api.Endpoints;

internal static class HealthEndpoints
{
    public static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/health", () => new HealthResponse("ok"))
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithName("Health")
            .WithTags("Health");

        return group;
    }
}

internal sealed record HealthResponse(string Status);
