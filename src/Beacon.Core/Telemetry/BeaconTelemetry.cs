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

    public const string LlmCallsInstrument = "beacon.llm.calls";
    public const string LlmTokensInstrument = "beacon.llm.tokens";

    /// <summary>The span of one LLM completion, from the request entering the queue to the provider's answer.</summary>
    public const string LlmCompletionActivityName = "beacon.llm.completion";

    /// <summary>Prefix of the ask stage spans: <c>beacon.ask.intent</c>, <c>beacon.ask.generation</c>, ….</summary>
    public const string AskStageActivityPrefix = "beacon.ask.";

    public const string LlmProviderTag = "gen_ai.provider.name";
    public const string LlmRequestModelTag = "gen_ai.request.model";
    public const string LlmResponseModelTag = "gen_ai.response.model";
    public const string LlmInputTokensTag = "gen_ai.usage.input_tokens";
    public const string LlmOutputTokensTag = "gen_ai.usage.output_tokens";
    public const string LlmCacheReadTokensTag = "gen_ai.usage.cache_read.input_tokens";
    public const string LlmCacheWriteTokensTag = "gen_ai.usage.cache_creation.input_tokens";
    public const string LlmTokenTypeTag = "gen_ai.token.type";
    public const string LlmOperationTag = "beacon.llm.operation";
    public const string LlmCacheHintTag = "beacon.llm.cache_hint";
    public const string LlmTruncatedTag = "beacon.llm.truncated";
    public const string AskStageTag = "beacon.ask.stage";
    public const string AskRepairTriggerTag = "beacon.ask.repair.trigger";
    public const string AskCandidatesTag = "beacon.ask.candidates";
    public const string AskTablesTag = "beacon.ask.tables";
    public const string AskContextCharsTag = "beacon.ask.context_chars";
    public const string AskTablesShownTag = "beacon.ask.tables_shown";
    public const string AskTablesOmittedTag = "beacon.ask.tables_omitted";
    public const string AskSourcesTag = "beacon.ask.sources";
    public const string AskIntentTag = "beacon.ask.intent";
    public const string AskStatementPurposeTag = "beacon.ask.statement.purpose";
    public const string AskProducedSqlTag = "beacon.ask.produced_sql";

    /// <summary>The innermost exception's type name when a span fails; <see cref="ErrorTypeTag"/> carries the outermost one.</summary>
    public const string ErrorRootTypeTag = "beacon.error.root_type";

    public const string TokenTypeInput = "input";
    public const string TokenTypeOutput = "output";
    public const string TokenTypeCacheRead = "cache_read";
    public const string TokenTypeCacheWrite = "cache_write";

    public const string AskStageIntent = "intent";
    public const string AskStageRouting = "routing";
    public const string AskStageGeneration = "generation";
    public const string AskStageSelfConsistency = "self_consistency";
    public const string AskStageRepair = "repair";
    public const string AskStageExecution = "execution";

    public const string OutcomeSuccess = "success";
    public const string OutcomeError = "error";
    public const string OutcomeCancelled = "cancelled";

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

    /// <summary>One per LLM completion; tagged provider, request model, operation, outcome and error type (errors only).</summary>
    public static readonly Counter<long> LlmCalls = Meter.CreateCounter<long>(
        LlmCallsInstrument, unit: "{call}", description: "LLM completions requested by Beacon.");

    /// <summary>
    /// Tokens of one LLM completion, one measurement per token type (input, output, cache_read, cache_write); tagged
    /// provider, request model, operation and token type.
    /// </summary>
    public static readonly Histogram<long> LlmTokens = Meter.CreateHistogram<long>(
        LlmTokensInstrument, unit: "{token}", description: "Tokens used by LLM completions.");

    /// <summary>Database errors that could not be screened in time and were replaced as a whole; never tagged with content.</summary>
    public static readonly Counter<long> ErrorRedactionFallbacks = Meter.CreateCounter<long>(
        ErrorRedactionFallbacksInstrument, unit: "{error}", description: "Database errors replaced as a whole because screening them timed out.");

    /// <summary>
    /// Starts the span of one ask stage (<c>beacon.ask.{stage}</c>, one of the <c>AskStage*</c> names) tagged with the
    /// stage name; null when nobody listens. Callers add counts only, never content.
    /// </summary>
    public static Activity? StartAskStage(string stage)
    {
        var activity = Source.StartActivity(AskStageActivityPrefix + stage);
        activity?.SetTag(AskStageTag, stage);

        return activity;
    }

    /// <summary>
    /// Marks a span whose work threw: a cancellation is outcome <see cref="OutcomeCancelled"/> with no error status; anything
    /// else is outcome <see cref="OutcomeError"/>, error status, <see cref="ErrorTypeTag"/> (the exception's type name) and
    /// <see cref="ErrorRootTypeTag"/> (the innermost exception's type name). Type names only, never a message.
    /// </summary>
    public static void RecordFailure(Activity? activity, Exception exception)
    {
        if (activity == null)
        {
            return;
        }

        if (exception is OperationCanceledException)
        {
            activity.SetTag(OutcomeTag, OutcomeCancelled);

            return;
        }

        activity.SetTag(OutcomeTag, OutcomeError);
        activity.SetTag(ErrorTypeTag, exception.GetType().Name);
        activity.SetTag(ErrorRootTypeTag, RootType(exception).Name);
        activity.SetStatus(ActivityStatusCode.Error);
    }

    /// <summary>Awaits a stage's work, recording on <paramref name="stage"/> (see <see cref="RecordFailure"/>) when it throws.</summary>
    public static async Task<T> ObserveStageAsync<T>(Activity? stage, Func<Task<T>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception ex)
        {
            RecordFailure(stage, ex);
            throw;
        }
    }

    /// <summary>The innermost exception's type, following <see cref="Exception.InnerException"/> to its end.</summary>
    public static Type RootType(Exception exception)
    {
        var root = exception;
        while (root.InnerException != null)
        {
            root = root.InnerException;
        }

        return root.GetType();
    }
}
