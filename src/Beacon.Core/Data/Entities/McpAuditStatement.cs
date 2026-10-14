using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// One statement an audited MCP tool call ran on a data source (the final query, a repair, a candidate, a grounding probe),
/// as a child of its <see cref="McpAuditLog"/> row, deleted with it. <see cref="SqlHash"/> is always kept; the statement
/// text (<see cref="Sql"/>) is content and is kept only while the project retains query content.
/// </summary>
public class McpAuditStatement : BaseEntity
{
    public int McpAuditLogId { get; set; }

    /// <summary>The audit row's project, so the content-retention lock of that project applies to the statement.</summary>
    public int? ProjectId { get; set; }

    public int? DataSourceId { get; set; }

    /// <summary>Why the statement ran (<c>McpStatementPurposes</c>: <c>initial</c>, <c>repair</c>, <c>candidate</c>, <c>probe</c>, <c>dry_run</c>, <c>source_step</c>, <c>query</c>).</summary>
    public required string Purpose { get; set; }

    /// <summary>Keyed hash of the statement text; kept whether or not the text is.</summary>
    public string? SqlHash { get; set; }

    /// <summary>The statement text: content, kept only while the project retains query content.</summary>
    public string? Sql { get; set; }

    /// <summary>The tables the statement read, as a JSON array of names.</summary>
    public string? TablesJson { get; set; }

    public int? RowCount { get; set; }

    public int? DurationMs { get; set; }

    /// <summary>The class of the error the statement ended with; null when it succeeded.</summary>
    public string? ErrorClass { get; set; }

    public McpAuditLog AuditLog { get; set; } = null!;
}
