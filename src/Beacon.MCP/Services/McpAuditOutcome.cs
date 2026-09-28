namespace Beacon.MCP.Services;

/// <summary>
/// Per-request (scoped) record of what <see cref="McpAuditService"/> managed to persist, read by
/// <see cref="McpAuditCallToolFilter"/> after the tool ran. <see cref="Failed"/> wins over <see cref="Written"/>:
/// one unwritten audit row is enough to withhold the result when the audit is required.
/// </summary>
internal sealed class McpAuditOutcome
{
    public bool Written { get; set; }

    public bool Failed { get; set; }
}
