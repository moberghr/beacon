namespace Beacon.SampleProject.Endpoints;

internal static class HealthEndpoints
{
    public static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/health", () => Results.Ok(new HealthResponse("ok")))
            .AllowAnonymous()
            .WithName("Health")
            .WithTags("Health")
            .Produces<HealthResponse>(StatusCodes.Status200OK);

        return group;
    }
}

internal sealed record HealthResponse(string Status);
