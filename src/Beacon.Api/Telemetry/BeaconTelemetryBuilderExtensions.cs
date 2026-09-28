using Beacon.Core.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Beacon.Api.Telemetry;

/// <summary>
/// Registers Beacon's own <see cref="BeaconTelemetry"/> instrumentation on a host's tracer/meter providers. This
/// project depends only on <c>OpenTelemetry.Api</c> — it never picks an exporter or a backend; the host does that
/// itself (SDK + exporter packages, <c>AddOpenTelemetry()</c>, <c>AddOtlpExporter()</c>, and so on) and calls these
/// extensions from its own tracing/metrics configuration.
/// </summary>
public static class BeaconTelemetryBuilderExtensions
{
    /// <summary>
    /// Adds Beacon's <c>"Beacon"</c> activity source, plus the MCP SDK's own <c>tools/call</c> source
    /// (<see cref="BeaconTelemetry.McpSdkSourceName"/>) that Beacon tags rather than duplicating, to the host's
    /// tracer provider.
    /// </summary>
    public static TracerProviderBuilder AddBeaconInstrumentation(this TracerProviderBuilder builder) =>
        builder.AddSource(BeaconTelemetry.ActivitySourceName, BeaconTelemetry.McpSdkSourceName);

    /// <summary>
    /// Adds Beacon's <c>"Beacon"</c> meter, plus the MCP SDK's meter (<see cref="BeaconTelemetry.McpSdkSourceName"/>),
    /// to the host's meter provider.
    /// </summary>
    public static MeterProviderBuilder AddBeaconInstrumentation(this MeterProviderBuilder builder) =>
        builder.AddMeter(BeaconTelemetry.MeterName, BeaconTelemetry.McpSdkSourceName);
}
