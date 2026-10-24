using System.Data.Common;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.Core.Configuration;
using Beacon.Core.Services;
using Beacon.Core.Services.Retention;
using Beacon.Core.Services.Security;
using Beacon.Core.Telemetry;
using Beacon.MCP.Tools;

namespace Beacon.MCP.Services;

/// <summary>
/// The audit of every tools/call (§1.7/§9.5) and its fail-closed decision (<c>Beacon:Mcp:Audit:Required</c>). Registered
/// as a call-tool request filter, so it wraps the whole tools/call pipeline — the attribute tools and every chained
/// <c>CallToolHandler</c> (saved-query and host endpoint tools). The playground / REST tool-run path
/// (<see cref="McpPlaygroundService"/>) runs its tools through the same <see cref="InvokeAsync"/>.
/// <para>
/// It runs the tool first (audit and signal are never short-circuited), then writes the call the tool kept on the scoped
/// <see cref="McpToolCallRecorder"/>: the audit row synchronously, then the signal for a SQL-carrying tool, on the success
/// and the failure path alike. A tool that threw after starting its record is recorded as failed and answers the
/// caller-safe error. A tool that writes its own row directly (not yet on the recorder) is not written a second time (its
/// signal still is); one that threw without a written audit row — a protocol error such as an unknown tool included —
/// gets one here, and its exception propagates unchanged.
/// </para>
/// <para>
/// Only then, when the audit is required and this request's audit row could not be written — or a successful result
/// produced no audit row at all — the result is replaced with an error: no unaudited data leaves Beacon. Otherwise the
/// tool's result passes through unchanged.
/// </para>
/// </summary>
internal static class McpAuditCallToolFilter
{
    public const string WithheldMessage =
        "Result withheld: the audit record for this call could not be written. Contact your Beacon administrator.";

    /// <summary>The error recorded for a call whose tool started its record but never gave it an outcome.</summary>
    internal const string IncompleteCallMessage = "The tool returned without recording an outcome.";

    private const string UnrecordedErrorMessage = "The tool returned an error.";

    private static readonly EventId ResultWithheldEvent = new(9101, "McpToolResultWithheld");

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create()
    {
        return next => async (request, cancellationToken) =>
            await InvokeAsync(request.Services, request.Params?.Name, x => next(request, x), cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="tool"/> and audits the call as the class describes. Shared by the SDK filter and the
    /// playground, so both write the same rows and take the same withhold decision.
    /// </summary>
    internal static async Task<CallToolResult> InvokeAsync(
        IServiceProvider? services,
        string? toolName,
        Func<CancellationToken, ValueTask<CallToolResult>> tool,
        CancellationToken cancellationToken)
    {
        var recorder = services?.GetService<McpToolCallRecorder>();
        CallToolResult? result = null;
        Exception? thrown = null;
        recorder?.BeginInvocation();
        try
        {
            result = await tool(cancellationToken);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
        finally
        {
            recorder?.EndInvocation();
        }

        if (thrown != null)
        {
            if (recorder?.HasCall != true)
            {
                await AuditUnrecordedFailureAsync(services, toolName, thrown);
                ExceptionDispatchInfo.Throw(thrown);
            }

            recorder.FailWith(thrown);

            // A protocol error stays a protocol error (the SDK answers it as a JSON-RPC error), written first.
            if (thrown is McpProtocolException)
            {
                await recorder.WriteAsync(CancellationToken.None);
                ExceptionDispatchInfo.Throw(thrown);
            }

            result = await RecordedFailureAsync(services, recorder, thrown);
        }

        // Written once: the audit row synchronously, never queued, so the decision below sees whether it was written.
        if (recorder?.HasCall == true)
        {
            CompleteFrom(recorder, result!);
            await recorder.WriteAsync(CancellationToken.None);
        }

        if (!ShouldWithhold(services, result!.IsError == true, toolName))
        {
            return result;
        }

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = WithheldMessage }]
        };
    }

    /// <summary>
    /// The one withhold decision shared by the SDK filter and the playground: withhold when the audit is required
    /// and either the audit write failed, or the result is a success (it may carry data) for which no audit row was
    /// written. An error result that was not audited passes through — it carries no data. Logs 9101 when withholding.
    /// </summary>
    internal static bool ShouldWithhold(IServiceProvider? services, bool resultIsError, string? toolName)
    {
        var required = services?.GetService<IOptions<McpDeploymentOptions>>()?.Value.Audit?.Required == true;
        if (!required)
        {
            return false;
        }

        var outcome = services!.GetService<McpAuditOutcome>();
        var failed = outcome?.Failed == true;
        var written = outcome?.Written == true;
        if (!failed && (written || resultIsError))
        {
            return false;
        }

        var logger = services
            .GetService<ILoggerFactory>()?
            .CreateLogger(BeaconTelemetry.AuditLogCategory);
        logger?.LogWarning(
            ResultWithheldEvent,
            "MCP tool result withheld: audit record not written. Tool={Tool} TraceId={TraceId}",
            McpAuditService.BoundedToolName(toolName),
            Activity.Current?.TraceId.ToHexString());

        return true;
    }

    // A tool that returned without giving its record an outcome: its result says which.
    private static void CompleteFrom(McpToolCallRecorder recorder, CallToolResult result)
    {
        if (recorder.IsCompleted)
        {
            return;
        }

        if (result.IsError != true)
        {
            recorder.Succeed();
            return;
        }

        var text = result.Content?
            .OfType<TextContentBlock>()
            .Select(x => x.Text)
            .FirstOrDefault(x => !string.IsNullOrEmpty(x));
        recorder.Fail(text ?? UnrecordedErrorMessage);
    }

    // The tool threw after starting its record: the record keeps the exception, the call answers what the tools' own
    // catch blocks answer — the caller-safe message, a database error's text through the database error redactor (it can
    // quote row values), the exception's type in the log, the detail in the audit log only.
    private static async Task<CallToolResult> RecordedFailureAsync(IServiceProvider? services, McpToolCallRecorder recorder, Exception exception)
    {
        var logger = services?
            .GetService<ILoggerFactory>()?
            .CreateLogger(typeof(McpAuditCallToolFilter));
        logger?.LogError(
            "MCP tool {Tool} failed with {ExceptionType} (detail in MCP audit log)",
            McpAuditService.BoundedToolName(recorder.Tool),
            exception.GetType().Name);

        if (exception is not DbException)
        {
            return ToolHelper.Error(ToolHelper.CallerSafeMessage(exception, recorder.Tool!));
        }

        var redact = await RedactsDatabaseErrorsAsync(services, recorder.ProjectId);
        var error = DatabaseErrorRedactor.ForCaller(exception.Message, recorder.RedactionSql, redact, logger);

        return ToolHelper.Error($"Query failed: {error}");
    }

    // A database error's text reaches the caller unredacted only while the project's PII detection is off and the
    // deployment does not force it on; a setting that cannot be read redacts.
    private static async Task<bool> RedactsDatabaseErrorsAsync(IServiceProvider? services, int? projectId)
    {
        if (services?.GetService<IOptions<McpDeploymentOptions>>()?.Value.ForcePiiDetection != false)
        {
            return true;
        }

        var settingsProvider = services.GetService<IMcpSettingsProvider>();
        if (settingsProvider == null)
        {
            return true;
        }

        try
        {
            var settings = await settingsProvider.GetEffectiveSettingsAsync(projectId ?? 0, CancellationToken.None);

            return settings.EnablePiiDetection;
        }
        catch (Exception)
        {
            return true;
        }
    }

    // A tool that threw without a record is audited all the same (§1.7), protocol errors (an unknown tool) included: the
    // tool name and the caller's Beacon user. The exception's message is kept only where the call's project is known (its
    // retention then decides, in the audit service); without one the row holds the error's class only. A row the tool
    // already wrote is not written twice; one whose write failed is written again.
    private static async Task AuditUnrecordedFailureAsync(IServiceProvider? services, string? toolName, Exception exception)
    {
        var audit = services?.GetService<McpAuditService>();
        var outcome = services?.GetService<McpAuditOutcome>();
        if (audit == null || outcome == null || outcome.Written)
        {
            return;
        }

        var projectContext = services!.GetService<IProjectContext>();
        var projectId = projectContext?.ActiveProjectId;
        var message = string.IsNullOrEmpty(exception.Message) ? exception.GetType().Name : exception.Message;
        var error = projectId != null ? message : McpContentRedactor.ErrorClassOf(message) ?? exception.GetType().Name;

        await outcome.WriteAsync(() => audit.LogToolCallAsync(
            userId: projectContext?.UserId,
            tool: toolName ?? McpAuditService.InvalidToolName,
            parameters: null,
            dataSourceId: null,
            projectId: projectId,
            executionTimeMs: 0,
            resultRowCount: null,
            errorMessage: error,
            ct: CancellationToken.None));
    }
}
