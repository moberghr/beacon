namespace Beacon.SampleProject.Endpoints;

internal static class BeaconApiEndpoints
{
    public static IEndpointRouteBuilder MapBeaconApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/beacon/api")
            .WithOpenApi();

        group.MapHealthEndpoints();
        group.MapAuthEndpoints();
        group.MapAntiforgeryEndpoints();

        return endpoints;
    }
}
