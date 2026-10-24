using Beacon.Core.Data.Entities;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// The grounding text a glossary term or a golden case puts into prompts, field by field, for the security audit: an event
/// lists the fields (<c>contentFields</c>) and a keyed digest of each one's stored text in the same order
/// (<c>beforeDigests</c> before the change, <c>afterDigests</c> after it; see <see cref="SecurityAuditIntegrity.Digest"/>),
/// so the sealed event shows which text changed, and lets a disclosed text be checked against it, without holding the
/// text itself.
/// </summary>
internal static class CurationContentAudit
{
    public const string FieldsKey = "contentFields";
    public const string BeforeKey = "beforeDigests";
    public const string AfterKey = "afterDigests";

    public static IReadOnlyList<string> GlossaryFields { get; } =
        ["term", "synonyms", "definition", "targetSchema", "targetTable", "targetColumn", "metricExpression"];

    public static IReadOnlyList<string> GoldenFields { get; } = ["question", "goldSql"];

    /// <summary>The term's grounding text, in the order of <see cref="GlossaryFields"/>.</summary>
    public static IReadOnlyList<string?> Of(McpGlossaryTerm term)
    {
        return [term.Term, term.Synonyms, term.Definition, term.TargetSchema, term.TargetTable, term.TargetColumn, term.MetricExpression];
    }

    /// <summary>The case's grounding text, in the order of <see cref="GoldenFields"/>.</summary>
    public static IReadOnlyList<string?> Of(McpEvalCase evalCase)
    {
        return [evalCase.Question, evalCase.GoldSql];
    }
}
