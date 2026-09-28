namespace Beacon.Core.Configuration;

/// <summary>Telemetry settings bound from <c>Beacon:Telemetry</c>. Defaults carry no content on spans.</summary>
public sealed class BeaconTelemetryOptions
{
    public const string SectionName = "Beacon:Telemetry";

    /// <summary>
    /// Put the tool input on the current span as <c>beacon.tool.input</c> — only where the project's content retention
    /// also allows content. Log events and metrics never carry content, whatever this says.
    /// </summary>
    public bool CaptureContent { get; set; }
}
