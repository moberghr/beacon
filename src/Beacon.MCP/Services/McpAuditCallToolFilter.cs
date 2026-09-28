using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.Core.Configuration;
using Beacon.Core.Telemetry;

namespace Beacon.MCP.Services;

/// <summary>
/// Fail-closed audit (<c>Beacon:Mcp:Audit:Required</c>). Registered as a call-tool request filter, so it wraps the
/// whole tools/call pipeline — the attribute tools and every chained <c>CallToolHandler</c> (saved-query and host
/// endpoint tools). The playground / REST tool-run path (<see cref="McpPlaygroundService"/>) applies the same
/// <see cref="ShouldWithhold"/> decision. It runs the tool first (audit and signal are never short-circuited,
/// §1.7/§9.5) and only then, when the audit is required and this request's audit row could not be written — or a
/// successful result produced no audit row at all — replaces the result with an error: no unaudited data leaves
/// Beacon. Otherwise the tool's result passes through unchanged.
/// </summary>
internal static class McpAuditCallToolFilter
{
    public const string WithheldMessage =
        "Result withheld: the audit record for this call could not be written. Contact your Beacon administrator.";

    private static readonly EventId ResultWithheldEvent = new(9101, "McpToolResultWithheld");

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create()
    {
        return next => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);

            if (!ShouldWithhold(request.Services, result.IsError == true, request.Params?.Name))
            {
                return result;
            }

            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = WithheldMessage }]
            };
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
}
