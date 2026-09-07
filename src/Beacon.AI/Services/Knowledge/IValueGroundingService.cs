namespace Beacon.AI.Services.Knowledge;

/// <summary>
/// The column shape ask-time value grounding ranks candidates against — deliberately independent of the
/// internal <see cref="SchemaColumn"/> so this seam (and its <c>ValueGroundingTable</c>/<c>McpSettingsData</c>
/// parameter) can stay a PUBLIC interface, which Moq's proxy generation for <c>Mock.Of</c>/<c>new Mock&lt;T&gt;</c>
/// requires (an internal interface would need <c>Beacon.AI</c> to grant <c>InternalsVisibleTo</c> to Castle's
/// dynamic proxy assembly). <see cref="KnowledgeGraphService"/> projects its internal <c>SchemaColumn</c> list
/// into this shape at the two call sites.
/// </summary>
public sealed record ValueGroundingColumn(
    string ColumnName, string DataType, bool IsPrimaryKey, int? MaxLength, string? SampleValuesJson);

/// <summary>
/// Table shape ask-time value grounding ranks candidate columns against — schema/table name and the
/// column metadata already loaded for the ask context (fast path: every table; smart path: the
/// detailed tables), so no extra metadata query is needed.
/// </summary>
public sealed record ValueGroundingTable(string Schema, string Table, IReadOnlyList<ValueGroundingColumn> Columns);

/// <summary>
/// Ask-time value grounding (spec item 5): extracts literal-looking tokens from the question, ranks
/// candidate string columns of the retrieved tables, and either resolves each literal from an
/// already-sampled column (no probe) or runs a bounded, read-only, identifier-whitelisted <c>LIKE</c>
/// probe against the live data source. Renders a "## Value matches" block the generation prompt can use
/// to pick exact filter values instead of guessing casing or spelling.
///
/// Security (§1.5, §1.6, §1.10, §1.11): every identifier comes from the metadata catalog only; every
/// literal is charset-whitelisted, quote-doubled, and wildcard-stripped before it reaches SQL; every
/// probe passes <c>SqlReadOnlyAstValidator</c> before execution; results are PII-screened before
/// rendering; neither a literal nor a probe's SQL is ever logged. Fails CLOSED — any error other than
/// cancellation is logged (a count only) and yields "" (2026-07-13 lesson).
/// </summary>
public interface IValueGroundingService
{
    Task<string> BuildValueMatchesBlockAsync(
        int dataSourceId,
        string question,
        IReadOnlyList<ValueGroundingTable> tables,
        McpSettingsData settings,
        CancellationToken ct);
}
