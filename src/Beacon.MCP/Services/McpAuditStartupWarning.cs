using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;

namespace Beacon.MCP.Services;

/// <summary>
/// Warns once at startup while <c>Beacon:Mcp:Audit:Required</c> is false (the default): a tool result whose audit row
/// could not be saved is still returned, and only the <c>Beacon.Audit</c> log event and the audit-failure metric record the
/// call. A deployment that needs every call on record sets it to true.
/// </summary>
internal sealed class McpAuditStartupWarning(
    IOptions<McpDeploymentOptions> options,
    ILogger<McpAuditStartupWarning> logger) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        if (options.Value.Audit?.Required != true)
        {
            logger.LogWarning(
                "{Setting} is false: an MCP tool result is returned even when its audit row could not be saved, and only the Beacon.Audit log event (Persisted=false) and the audit write failure metric record that call. Set it to true to withhold such a result.",
                $"{McpDeploymentOptions.SectionName}:Audit:{nameof(McpAuditOptions.Required)}");
        }

        return next;
    }
}
