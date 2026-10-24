using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;

namespace Beacon.Api;

/// <summary>
/// Notes once at startup, at Information, when no retention is configured: neither <c>Beacon:Retention</c> (rows or
/// content) nor <c>Beacon:Mcp:Audit:RetentionDays</c> sets a window, so run history, logs and the MCP audit are kept
/// forever. Names the settings and the recommended values; logs no values from data (§1.11).
/// </summary>
internal sealed class RetentionStartupNote(
    ILogger<RetentionStartupNote> logger,
    IOptions<RetentionOptions>? retention = null,
    IOptions<McpDeploymentOptions>? mcp = null) : IStartupFilter
{
    internal static readonly EventId UnconfiguredEvent = new(9123, "RetentionUnconfigured");

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            Note();
            next(builder);
        };
    }

    internal void Note()
    {
        if (retention == null || !IsUnconfigured(retention.Value, mcp?.Value))
        {
            return;
        }

        logger.LogInformation(
            UnconfiguredEvent,
            "No retention is configured ({Rows}, {Content}, {McpAudit}): run history, logs and the MCP audit are kept forever. Recommended: {RecommendedDays} days for rows, {RecommendedMcpAuditDays} for the MCP audit, {RecommendedContentDays} for content in regulated deployments; see the Data retention section of the configuration docs",
            $"{RetentionOptions.SectionName}:{nameof(RetentionOptions.Days)}",
            $"{RetentionOptions.SectionName}:{nameof(RetentionOptions.ContentDays)}",
            $"{McpDeploymentOptions.SectionName}:Audit:{nameof(McpAuditOptions.RetentionDays)}",
            RetentionOptions.RecommendedDays,
            McpAuditOptions.RecommendedRetentionDays,
            RetentionOptions.RecommendedRegulatedContentDays);
    }

    internal static bool IsUnconfigured(RetentionOptions retention, McpDeploymentOptions? mcp)
    {
        return retention.Days == 0
            && retention.ContentDays == 0
            && retention.Targets.Values.All(x => x == 0)
            && mcp?.Audit?.RetentionDays is null or 0;
    }
}
