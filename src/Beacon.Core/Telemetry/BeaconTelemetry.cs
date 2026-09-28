using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Beacon.Core.Telemetry;

/// <summary>
/// Beacon's own telemetry surface: the ActivitySource/Meter <c>"Beacon"</c>, its instruments and the tag names.
/// BCL-only (<c>System.Diagnostics</c>), so Core takes no OpenTelemetry dependency; a host opts in by adding the
/// source and meter to its own tracer/meter providers. The MCP SDK already opens one span per <c>tools/call</c>
/// (<see cref="McpSdkSourceName"/>) — Beacon tags that span rather than opening a duplicate, and records no duration
/// instrument because the SDK's <c>mcp.server.operation.duration</c> covers it. No tag ever carries content (§1.11).
/// </summary>
public static class BeaconTelemetry
{
    public const string ActivitySourceName = "Beacon";
    public const string MeterName = "Beacon";
    public const string McpSdkSourceName = "Experimental.ModelContextProtocol";

    /// <summary>Logger category of the audit log stream (EventIds 9100-9103): identifiers and counts, never content.</summary>
    public const string AuditLogCategory = "Beacon.Audit";

    public const string ToolCallsInstrument = "beacon.mcp.tool.calls";
    public const string ToolRowsInstrument = "beacon.mcp.tool.rows";
    public const string AuditWriteFailuresInstrument = "beacon.mcp.audit.write_failures";

    public const string ToolNameTag = "gen_ai.tool.name";
    public const string OutcomeTag = "beacon.outcome";
    public const string ErrorTypeTag = "error.type";
    public const string ProjectIdTag = "beacon.project.id";
    public const string DataSourceIdTag = "beacon.data_source.id";
    public const string CallerKindTag = "beacon.caller.kind";
    public const string CallerHashTag = "beacon.caller.hash";
    public const string ResultRowsTag = "beacon.result.rows";
    public const string ErrorClassTag = "beacon.error.class";
    public const string AuditPersistedTag = "beacon.audit.persisted";
    public const string ToolInputTag = "beacon.tool.input";

    public const string OutcomeSuccess = "success";
    public const string OutcomeError = "error";

    public static readonly ActivitySource Source = new(ActivitySourceName);

    public static readonly Meter Meter = new(MeterName);

    /// <summary>One per audited tool call; tagged tool name, outcome, error class (errors only) and caller kind.</summary>
    public static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>(
        ToolCallsInstrument, unit: "{call}", description: "MCP tool calls audited by Beacon.");

    /// <summary>Result rows of a tool call that reports a row count; tagged tool name.</summary>
    public static readonly Histogram<long> ToolRows = Meter.CreateHistogram<long>(
        ToolRowsInstrument, unit: "{row}", description: "Rows returned by MCP tool calls.");

    /// <summary>Audit rows that could not be written; tagged tool name.</summary>
    public static readonly Counter<long> AuditWriteFailures = Meter.CreateCounter<long>(
        AuditWriteFailuresInstrument, unit: "{failure}", description: "MCP audit rows that could not be persisted.");
}
