using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// Admin-managed business glossary entry (term → synonyms → definition → target column/metric).
/// Project-scoped, optionally data-source-scoped. Embedded for masked-question retrieval
/// (via <see cref="McpEmbedding"/>, OwnerType=GlossaryTerm) and injected into the smart context.
/// </summary>
public class McpGlossaryTerm : BaseEntity
{
    public int ProjectId { get; set; }
    public int? DataSourceId { get; set; }

    public required string Term { get; set; }
    public string? Synonyms { get; set; }
    public required string Definition { get; set; }

    public string? TargetSchema { get; set; }
    public string? TargetTable { get; set; }
    public string? TargetColumn { get; set; }
    public string? MetricExpression { get; set; }

    public bool IsActive { get; set; } = true;
}
