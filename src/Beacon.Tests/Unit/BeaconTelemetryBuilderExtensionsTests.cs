using FluentAssertions;
using NUnit.Framework;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Beacon.Api.Telemetry;
using Beacon.Core.Telemetry;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC13 — <see cref="BeaconTelemetryBuilderExtensions.AddBeaconInstrumentation(TracerProviderBuilder)"/> and its
/// meter overload register both the <c>"Beacon"</c> source/meter and the MCP SDK's own
/// <c>"Experimental.ModelContextProtocol"</c> source/meter. <see cref="TracerProviderBuilder"/> and
/// <see cref="MeterProviderBuilder"/> are abstract with a protected constructor (OpenTelemetry.Api only, no SDK
/// package needed here) — recording test doubles capture exactly what the extension registers, without building a
/// full exporter pipeline.
/// </summary>
[TestFixture]
public class BeaconTelemetryBuilderExtensionsTests
{
    [Test]
    public void AddBeaconInstrumentation_OnATracerProviderBuilder_AddsTheBeaconAndMcpSdkSources()
    {
        var builder = new RecordingTracerProviderBuilder();

        var result = builder.AddBeaconInstrumentation();

        result.Should().BeSameAs(builder, "the extension returns the same builder for fluent chaining");
        builder.Sources.Should().Equal(BeaconTelemetry.ActivitySourceName, BeaconTelemetry.McpSdkSourceName);
    }

    [Test]
    public void AddBeaconInstrumentation_OnAMeterProviderBuilder_AddsTheBeaconAndMcpSdkMeters()
    {
        var builder = new RecordingMeterProviderBuilder();

        var result = builder.AddBeaconInstrumentation();

        result.Should().BeSameAs(builder, "the extension returns the same builder for fluent chaining");
        builder.Meters.Should().Equal(BeaconTelemetry.MeterName, BeaconTelemetry.McpSdkSourceName);
    }

    private sealed class RecordingTracerProviderBuilder : TracerProviderBuilder
    {
        public List<string> Sources { get; } = [];

        public override TracerProviderBuilder AddSource(params string[] names)
        {
            Sources.AddRange(names);

            return this;
        }

        public override TracerProviderBuilder AddLegacySource(string operationName) => this;

        public override TracerProviderBuilder AddInstrumentation<TInstrumentation>(Func<TInstrumentation> instrumentationFactory) => this;
    }

    private sealed class RecordingMeterProviderBuilder : MeterProviderBuilder
    {
        public List<string> Meters { get; } = [];

        public override MeterProviderBuilder AddMeter(params string[] names)
        {
            Meters.AddRange(names);

            return this;
        }

        public override MeterProviderBuilder AddInstrumentation<TInstrumentation>(Func<TInstrumentation> instrumentationFactory) => this;
    }
}
