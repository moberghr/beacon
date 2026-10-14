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

    /// <summary>
    /// Logger category of the audit log stream: MCP tool calls and their audit reads and purges (EventIds 9100-9103), and the
    /// security audit trail's copy of every event, its reads, purges, write failures, verification failures, checkpoints and
    /// failed purges (9104-9110). Identifiers, counts and digests, never content.
    /// </summary>
    public const string AuditLogCategory = "Beacon.Audit";

    public const string ToolCallsInstrument = "beacon.mcp.tool.calls";
    public const string ToolRowsInstrument = "beacon.mcp.tool.rows";
    public const string AuditWriteFailuresInstrument = "beacon.mcp.audit.write_failures";
    public const string ErrorRedactionFallbacksInstrument = "beacon.error_redaction.fallbacks";
    public const string SecurityAuditWriteFailuresInstrument = "beacon.security_audit.write_failures";
    public const string SecurityAuditVerificationFailuresInstrument = "beacon.security_audit.verification_failures";

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
    public const string SecurityAuditActionTag = "beacon.security_audit.action";

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

    /// <summary>
    /// Security audit events that could not be written on their own (a sign-in, a refusal, a read), and retention purges
    /// that did not run (action <c>audit.purged</c>); tagged action.
    /// </summary>
    public static readonly Counter<long> SecurityAuditWriteFailures = Meter.CreateCounter<long>(
        SecurityAuditWriteFailuresInstrument, unit: "{failure}", description: "Security audit events that could not be persisted.");

    /// <summary>
    /// Stored security audit events a scheduled verification or a retention purge found changed, under an unknown key or
    /// dated out of range; tagged action (<c>audit.verified</c>, <c>audit.purged</c>).
    /// </summary>
    public static readonly Counter<long> SecurityAuditVerificationFailures = Meter.CreateCounter<long>(
        SecurityAuditVerificationFailuresInstrument, unit: "{event}", description: "Stored security audit events that failed verification.");

    /// <summary>Database errors that could not be screened in time and were replaced as a whole; never tagged with content.</summary>
    public static readonly Counter<long> ErrorRedactionFallbacks = Meter.CreateCounter<long>(
        ErrorRedactionFallbacksInstrument, unit: "{error}", description: "Database errors replaced as a whole because screening them timed out.");
}
