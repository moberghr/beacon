using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.SavedQueries;
using Beacon.Core.Services;
using Beacon.Core.Services.Retention;
using Beacon.Core.Telemetry;
using Beacon.MCP.Discovery;
using Beacon.MCP.HostEndpoints;
using Beacon.MCP.SavedQueries;

namespace Beacon.MCP.Services;

internal sealed partial class McpAuditService(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider,
    IHttpContextAccessor httpContextAccessor,
    IOptions<McpDeploymentOptions> deploymentOptions,
    ILogger<McpAuditService> logger,
    McpAuditOutcome outcome,
    IOptions<BeaconTelemetryOptions> telemetryOptions,
    ILoggerFactory loggerFactory)
{
    internal const string McpSessionIdHeader = "Mcp-Session-Id";
    internal static readonly EventId ToolAuditedEvent = new(9100, "McpToolAudited");
    private const int MaxHeaderIdLength = 128;
    private const int MaxContentLength = 4000;
    internal const string InvalidToolName = "<invalid>";

    private readonly ILogger _auditLogger = loggerFactory.CreateLogger(BeaconTelemetry.AuditLogCategory);

    public async Task LogToolCallAsync(int? sessionId, int? userId, string tool, string? parameters,
        int? dataSourceId, int? projectId, int executionTimeMs, int? resultRowCount, string? errorMessage,
        IReadOnlyList<string>? tables = null, CancellationToken ct = default)
    {
        var retainContent = false;
        McpAuditLog? entry = null;
        var persisted = false;

        try
        {
            // §1.7 — the audit row is written whatever happens here. A settings/cache failure must not lose the
            // row, so it resolves in its own try and fails CLOSED (structural form) rather than aborting the write.
            retainContent = await RetainsContentAsync(projectId, ct);

            string? loggedParameters;
            string? loggedErrorMessage;
            if (!retainContent)
            {
                loggedParameters = McpContentRedactor.StructuralAuditParameters(tool, parameters, tables);
                loggedErrorMessage = McpContentRedactor.ErrorClassOf(errorMessage);
            }
            else
            {
                loggedParameters = Truncate(parameters);
                loggedErrorMessage = Truncate(errorMessage);
            }

            // Minted by JwtBearerAuthMiddleware from IMcpCallerMapper; token-supplied values are stripped there.
            var caller = httpContextAccessor.HttpContext?.User;
            var callerKind = caller?.FindFirst(McpCallerClaimTypes.CallerKind)?.Value;
            var callerHash = caller?.FindFirst(McpCallerClaimTypes.CallerHash)?.Value;
            var apiKeyId = int.TryParse(caller?.FindFirst(McpCallerClaimTypes.ApiKeyId)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedApiKeyId)
                ? parsedApiKeyId
                : (int?)null;

            var activity = Activity.Current;
            var headers = httpContextAccessor.HttpContext?.Request.Headers;
            var mcpSessionId = headers is null ? null : SanitizeHeaderId(headers[McpSessionIdHeader]);
            var requestIdHeader = deploymentOptions.Value.Audit?.RequestIdHeader;
            var upstreamRequestId = headers is null || string.IsNullOrEmpty(requestIdHeader)
                ? null
                : SanitizeHeaderId(headers[requestIdHeader]);

            entry = new McpAuditLog
            {
                SessionId = sessionId,
                UserId = userId,
                Tool = tool,
                Parameters = loggedParameters,
                DataSourceId = dataSourceId,
                ProjectId = projectId,
                ExecutionTimeMs = executionTimeMs,
                ResultRowCount = resultRowCount,
                ErrorMessage = loggedErrorMessage,
                CallerKind = callerKind,
                CallerHash = callerHash,
                TraceId = activity?.TraceId.ToHexString(),
                SpanId = activity?.SpanId.ToHexString(),
                McpSessionId = mcpSessionId,
                UpstreamRequestId = upstreamRequestId,
                ApiKeyId = apiKeyId
            };

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            context.McpAuditLogs.Add(entry);
            await context.SaveChangesAsync(ct);
            persisted = true;
            outcome.Written = true;
        }
        catch (Exception ex)
        {
            // §1.7 — audit logging is non-optional. Swallow so a transient DB issue doesn't fail the
            // tool call, but log at Error so a sustained audit-sink outage is operationally visible. The outcome
            // flag lets McpAuditCallToolFilter withhold the result when the deployment requires the audit.
            outcome.Failed = true;
            logger.LogError(ex, "Failed to log MCP audit entry for tool {Tool}", BoundedToolName(tool));
        }

        EmitTelemetry(entry, persisted, retainContent, tool, userId, projectId, dataSourceId, executionTimeMs,
            resultRowCount, errorMessage, parameters);
    }

    private async Task<bool> RetainsContentAsync(int? projectId, CancellationToken ct)
    {
        try
        {
            var settings = await settingsProvider.GetEffectiveSettingsAsync(projectId ?? 0, ct);

            return ContentRetentionDecision.From(settings).RetainQueryContent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Content retention settings unavailable for project {ProjectId}; auditing structurally.", projectId);

            return false;
        }
    }

    // §1.11 — the Beacon.Audit event, span tags and metrics carry identifiers and counts only: never parameters, SQL,
    // question or the raw error (ErrorClassOf). The one exception is beacon.tool.input on the span, behind the double
    // opt-in CaptureContent AND the project's retain-content decision. Telemetry must never lose or fail the audit.
    private void EmitTelemetry(McpAuditLog? entry, bool persisted, bool retainContent, string tool, int? userId,
        int? projectId, int? dataSourceId, int executionTimeMs, int? resultRowCount, string? errorMessage,
        string? parameters)
    {
        try
        {
            var errorClass = McpContentRedactor.ErrorClassOf(errorMessage);
            var callerKind = entry?.CallerKind;
            var callerHash = entry?.CallerHash;
            var activity = Activity.Current;
            var traceId = entry?.TraceId ?? activity?.TraceId.ToHexString();
            var boundedTool = BoundedToolName(tool);
            var metricTool = MetricToolName(tool);

            _auditLogger.Log(
                LogLevel.Information,
                ToolAuditedEvent,
                "MCP tool audited: Tool={Tool} AuditId={AuditId} Persisted={Persisted} ProjectId={ProjectId} " +
                "DataSourceId={DataSourceId} UserId={UserId} ApiKeyId={ApiKeyId} CallerKind={CallerKind} " +
                "CallerHash={CallerHash} DurationMs={DurationMs} Rows={Rows} ErrorClass={ErrorClass} " +
                "TraceId={TraceId} McpSessionId={McpSessionId} UpstreamRequestId={UpstreamRequestId}",
                boundedTool,
                persisted ? entry?.Id : null,
                persisted,
                projectId,
                dataSourceId,
                userId,
                entry?.ApiKeyId,
                callerKind,
                callerHash,
                executionTimeMs,
                resultRowCount,
                errorClass,
                traceId,
                entry?.McpSessionId,
                entry?.UpstreamRequestId);

            if (activity != null)
            {
                activity.SetTag(BeaconTelemetry.ProjectIdTag, projectId);
                activity.SetTag(BeaconTelemetry.DataSourceIdTag, dataSourceId);
                activity.SetTag(BeaconTelemetry.CallerKindTag, callerKind);
                activity.SetTag(BeaconTelemetry.CallerHashTag, callerHash);
                activity.SetTag(BeaconTelemetry.ResultRowsTag, resultRowCount);
                activity.SetTag(BeaconTelemetry.ErrorClassTag, errorClass);
                activity.SetTag(BeaconTelemetry.AuditPersistedTag, persisted);

                if (telemetryOptions.Value.CaptureContent && retainContent && parameters != null)
                {
                    activity.SetTag(BeaconTelemetry.ToolInputTag, Truncate(parameters));
                }
            }

            var toolTag = new KeyValuePair<string, object?>(BeaconTelemetry.ToolNameTag, metricTool);
            var callTags = new TagList
            {
                toolTag,
                { BeaconTelemetry.OutcomeTag, string.IsNullOrEmpty(errorMessage) ? BeaconTelemetry.OutcomeSuccess : BeaconTelemetry.OutcomeError }
            };

            if (!string.IsNullOrEmpty(errorClass))
            {
                callTags.Add(BeaconTelemetry.ErrorTypeTag, errorClass);
            }

            if (callerKind != null)
            {
                callTags.Add(BeaconTelemetry.CallerKindTag, callerKind);
            }

            BeaconTelemetry.ToolCalls.Add(1, callTags);

            if (resultRowCount.HasValue)
            {
                BeaconTelemetry.ToolRows.Record(resultRowCount.Value, toolTag);
            }

            if (!persisted)
            {
                BeaconTelemetry.AuditWriteFailures.Add(1, toolTag);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to emit MCP audit telemetry for tool {Tool}", BoundedToolName(tool));
        }
    }

    // §1.11 — tool names can be caller-chosen (e.g. an unknown q_<anything> saved-query name), so the 9100/9101 LOG
    // fields only ever see a short, whitelisted name. Logs are not a cardinality problem, so this bound (charset +
    // length) is enough there. The DB row keeps the raw name.
    internal static string BoundedToolName(string? tool) =>
        tool != null && ToolNamePattern().IsMatch(tool) ? tool : InvalidToolName;

    // §1.11 — the gen_ai.tool.name METRIC tag is a cardinality risk a charset bound does not fix: a well-formed but
    // caller-chosen name (q_1, q_2, ... each a distinct saved-query name) would still open a new series per name.
    // The metric tag is instead drawn from a closed set: a built-in tool (the attribute tools plus the fixed
    // search_saved_queries/run_saved_query/search_api/call_api dispatcher names) keeps its own value; anything else
    // collapses to its prefix bucket (q_* / api_*) or, failing that, "<other>".
    internal const string OtherToolMetricTag = "<other>";
    internal const string SavedQueryToolMetricTag = "q_*";
    internal const string HostEndpointToolMetricTag = "api_*";

    private static readonly HashSet<string> KnownMetricToolNames = new(
        McpToolCatalog.Names
            .Append(SavedQueryToolService.SearchToolName)
            .Append(SavedQueryToolService.RunToolName)
            .Append(HostEndpointToolService.SearchToolName)
            .Append(HostEndpointToolService.CallToolName),
        StringComparer.Ordinal);

    internal static string MetricToolName(string? tool)
    {
        if (tool != null && KnownMetricToolNames.Contains(tool))
        {
            return tool;
        }

        if (tool != null && tool.StartsWith(SavedQueryToolRules.ToolNamePrefix, StringComparison.Ordinal))
        {
            return SavedQueryToolMetricTag;
        }

        if (tool != null && tool.StartsWith(HostEndpointToolDescriptor.ToolNamePrefix, StringComparison.Ordinal))
        {
            return HostEndpointToolMetricTag;
        }

        return OtherToolMetricTag;
    }

    private static string? Truncate(string? value) =>
        value?.Length > MaxContentLength ? value[..MaxContentLength] : value;

    // §1.11 — a caller-supplied id reaches the table (and later the log stream) only when it is short and in a
    // whitelisted charset; anything else is dropped rather than truncated, which blocks log injection.
    private static string? SanitizeHeaderId(string? value) =>
        value is { Length: > 0 and <= MaxHeaderIdLength } && HeaderIdPattern().IsMatch(value)
            ? value
            : null;

    [GeneratedRegex(@"^[A-Za-z0-9._:\-]+\z")]
    private static partial Regex HeaderIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.:\-]{1,200}\z")]
    private static partial Regex ToolNamePattern();
}
