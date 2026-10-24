using System.Diagnostics;
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
    /// Adds Beacon's <c>"Beacon"</c> activity source to the host's tracer provider. The MCP SDK's own <c>tools/call</c>
    /// spans (<see cref="BeaconTelemetry.McpSdkSourceName"/>), which Beacon tags, are added only by
    /// <see cref="AddBeaconMcpSdkTracing"/>.
    /// </summary>
    public static TracerProviderBuilder AddBeaconInstrumentation(this TracerProviderBuilder builder) =>
        builder.AddSource(BeaconTelemetry.ActivitySourceName);

    /// <summary>
    /// Adds the MCP SDK's own span per <c>tools/call</c> (<see cref="BeaconTelemetry.McpSdkSourceName"/>, the span Beacon
    /// tags) to the host's tracer provider. The SDK writes an error result's content or an exception's message into the
    /// span's status description; this clears every such description when the span ends, before the provider's processors
    /// and exporters see it (the status code stays). Call it on the same builder as the exporter.
    /// </summary>
    public static TracerProviderBuilder AddBeaconMcpSdkTracing(this TracerProviderBuilder builder) =>
        builder
            .AddInstrumentation(() => new McpSdkSpanStatusScrubber())
            .AddSource(BeaconTelemetry.McpSdkSourceName);

    /// <summary>
    /// Adds Beacon's <c>"Beacon"</c> meter, plus the MCP SDK's meter (<see cref="BeaconTelemetry.McpSdkSourceName"/>),
    /// to the host's meter provider.
    /// </summary>
    public static MeterProviderBuilder AddBeaconInstrumentation(this MeterProviderBuilder builder) =>
        builder.AddMeter(BeaconTelemetry.MeterName, BeaconTelemetry.McpSdkSourceName);
}

/// <summary>
/// Clears the status description of every MCP SDK span when it ends. Created by the tracer provider when it is built,
/// before the provider starts listening, so its listener runs first when a span stops; it never takes part in sampling.
/// Disposed with the provider.
/// </summary>
internal sealed class McpSdkSpanStatusScrubber : IDisposable
{
    private readonly ActivityListener _listener;

    public McpSdkSpanStatusScrubber()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = x => x.Name == BeaconTelemetry.McpSdkSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.None,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.None,
            ActivityStopped = Scrub
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    internal static void Scrub(Activity activity)
    {
        if (activity.StatusDescription != null)
        {
            activity.SetStatus(activity.Status);
        }
    }
}
