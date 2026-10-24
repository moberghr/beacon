using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Beacon.Api.Telemetry;
using Beacon.Core.Telemetry;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC13 — <see cref="BeaconTelemetryBuilderExtensions.AddBeaconInstrumentation(TracerProviderBuilder)"/> registers the
/// <c>"Beacon"</c> source and its meter overload the <c>"Beacon"</c> meter and the MCP SDK's own
/// <c>"Experimental.ModelContextProtocol"</c> meter; the SDK's spans come only with
/// <see cref="BeaconTelemetryBuilderExtensions.AddBeaconMcpSdkTracing"/>, their status descriptions cleared. <see cref="TracerProviderBuilder"/> and
/// <see cref="MeterProviderBuilder"/> are abstract with a protected constructor (OpenTelemetry.Api only, no SDK
/// package needed here) — recording test doubles capture exactly what the extension registers, without building a
/// full exporter pipeline.
/// </summary>
[TestFixture]
public class BeaconTelemetryBuilderExtensionsTests
{
    [Test]
    public void AddBeaconInstrumentation_OnATracerProviderBuilder_AddsTheBeaconSourceOnly()
    {
        var builder = new RecordingTracerProviderBuilder();

        var result = builder.AddBeaconInstrumentation();

        result.Should().BeSameAs(builder, "the extension returns the same builder for fluent chaining");
        builder.Sources.Should().Equal(BeaconTelemetry.ActivitySourceName);
    }

    [Test]
    public void AddBeaconMcpSdkTracing_AddsTheMcpSdkSource_WithTheStatusScrubber()
    {
        var builder = new RecordingTracerProviderBuilder();

        var result = builder.AddBeaconMcpSdkTracing();

        result.Should().BeSameAs(builder);
        builder.Sources.Should().Equal(BeaconTelemetry.McpSdkSourceName);
        builder.Instrumentations.Should().Equal(typeof(McpSdkSpanStatusScrubber));
    }

    [Test]
    public void AnMcpSdkSpansStatusDescription_IsClearedBeforeTheProvidersProcessorsSeeIt()
    {
        var ended = EndedSpans(x => x.AddBeaconMcpSdkTracing());

        ended.Should().ContainSingle().Which.Should().Be((ActivityStatusCode.Error, (string?)null));
    }

    [Test]
    public void WithoutTheScrubber_TheProvidersProcessorsSeeTheStatusDescription()
    {
        var ended = EndedSpans(x => x.AddSource(BeaconTelemetry.McpSdkSourceName));

        ended.Should().ContainSingle().Which.Should().Be((ActivityStatusCode.Error, (string?)"tool error content"));
    }

    // The status of each MCP SDK span the provider's processors saw end, the provider built with configure.
    private static List<(ActivityStatusCode Code, string? Description)> EndedSpans(Func<TracerProviderBuilder, TracerProviderBuilder> configure)
    {
        var ended = new List<(ActivityStatusCode, string?)>();
        using (configure(Sdk.CreateTracerProviderBuilder()).AddProcessor(new EndCapture(ended)).Build())
        {
            using var source = new ActivitySource(BeaconTelemetry.McpSdkSourceName);
            using var activity = source.StartActivity("tools/call query");
            activity!.SetStatus(ActivityStatusCode.Error, "tool error content");
        }

        return ended;
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

        public List<Type> Instrumentations { get; } = [];

        public override TracerProviderBuilder AddInstrumentation<TInstrumentation>(Func<TInstrumentation> instrumentationFactory)
        {
            Instrumentations.Add(typeof(TInstrumentation));

            return this;
        }
    }

    private sealed class EndCapture(List<(ActivityStatusCode, string?)> ended) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            ended.Add((data.Status, data.StatusDescription));
        }
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
