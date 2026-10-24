using Microsoft.AspNetCore.Builder;

namespace Beacon.Api.Authentication;

/// <summary>
/// Endpoint metadata for a route that runs long work inline (a migration run with its own per-job timeout, a metadata
/// scan, an LLM flow of several calls). <see cref="BeaconRequestLimitsMiddleware"/> leaves the request's cancellation
/// alone on such a route: the work owns its own timeout. The body limit still applies.
/// </summary>
public sealed class BeaconLongRunningMetadata
{
    internal static readonly BeaconLongRunningMetadata Instance = new();

    private BeaconLongRunningMetadata()
    {
    }
}

public static class BeaconLongRunningEndpointExtensions
{
    /// <summary>Exempts the route from <c>Beacon:RequestLimits:ApiTimeoutSeconds</c>.</summary>
    public static TBuilder WithBeaconLongRunning<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(x => x.Metadata.Add(BeaconLongRunningMetadata.Instance));

        return builder;
    }
}
