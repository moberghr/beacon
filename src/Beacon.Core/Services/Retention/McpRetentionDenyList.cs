using System.Reflection;
using Beacon.Core.Data.Entities;

namespace Beacon.Core.Services.Retention;

public enum RetentionKind
{
    /// <summary>Interaction content — nulled under the lock (empty string when the property is <c>required</c>).</summary>
    Content,

    /// <summary>Free-text error — replaced by its error class under the lock.</summary>
    ErrorClass,

    /// <summary>Structure, configuration or documentation — always kept.</summary>
    Structural
}

/// <param name="AlreadyRedacted">
/// CONVENTION (review F002): any brace that writes a non-null PLACEHOLDER for a Content property under the lock —
/// rather than nulling it — must register a matching predicate here in the same change, or the belt will destroy
/// what the brace wrote. The reflection test (SC2) checks classification completeness, not transform shape, so it
/// cannot catch this for you.
///
/// Optional predicate: when it returns true for the current value, the value IS the redacted form and the belt
/// leaves it alone. Needed where a brace rewrites a Content property into a structural shape rather than nulling
/// it (McpAuditLog.Parameters), so the belt cannot destroy what the brace just wrote.
/// </param>
public sealed record McpRetentionRule(Type Entity, string Property, RetentionKind Kind, Func<string, bool>? AlreadyRedacted = null);

/// <summary>
/// The single place every string property of every <c>Mcp*</c> entity is classified. The reflection test
/// (<c>McpRetentionDenyListTests</c>) fails if a new column ships unclassified, and
/// <c>ContentRetentionInterceptor</c> is driven entirely by this table.
/// </summary>
public static class McpRetentionDenyList
{
    public static IReadOnlyList<McpRetentionRule> Rules { get; } =
    [
        // McpQuerySignal — the learning signal: everything the caller said or the model produced is content.
        Rule<McpQuerySignal>(nameof(McpQuerySignal.Tool), RetentionKind.Structural),
        Rule<McpQuerySignal>(nameof(McpQuerySignal.Question), RetentionKind.Content),
        Rule<McpQuerySignal>(nameof(McpQuerySignal.IntentClassification), RetentionKind.Structural),
        Rule<McpQuerySignal>(nameof(McpQuerySignal.RoutingDecision), RetentionKind.Content),
        Rule<McpQuerySignal>(nameof(McpQuerySignal.GeneratedSql), RetentionKind.Content),
        Rule<McpQuerySignal>(nameof(McpQuerySignal.TablesUsed), RetentionKind.Structural),
        Rule<McpQuerySignal>(nameof(McpQuerySignal.ColumnsUsed), RetentionKind.Structural),
        Rule<McpQuerySignal>(nameof(McpQuerySignal.SchemaValidationError), RetentionKind.ErrorClass),
        Rule<McpQuerySignal>(nameof(McpQuerySignal.ExecutionError), RetentionKind.ErrorClass),
        Rule<McpQuerySignal>(nameof(McpQuerySignal.DryRunError), RetentionKind.ErrorClass),
        Rule<McpQuerySignal>(nameof(McpQuerySignal.CorrectedSql), RetentionKind.Content),
        Rule<McpQuerySignal>(nameof(McpQuerySignal.UserCorrectedSql), RetentionKind.Content),
        Rule<McpQuerySignal>(nameof(McpQuerySignal.FeedbackNote), RetentionKind.Content),
        Rule<McpQuerySignal>(nameof(McpQuerySignal.CallerHash), RetentionKind.Structural),

        // McpAuditLog — the row itself is never skipped (§1.7); under the lock Parameters becomes the structural JSON.
        Rule<McpAuditLog>(nameof(McpAuditLog.Tool), RetentionKind.Structural),
        Rule<McpAuditLog>(nameof(McpAuditLog.Parameters), RetentionKind.Content, McpContentRedactor.IsStructuralAuditParameters),
        Rule<McpAuditLog>(nameof(McpAuditLog.ErrorMessage), RetentionKind.ErrorClass),

        // McpLearnedPattern — PatternContent is Structural WITH A GUARD: under the lock it is only ever the
        // deterministic schema/table/column template (the LLM lesson extractor is disabled). The examples are
        // verbatim user content.
        Rule<McpLearnedPattern>(nameof(McpLearnedPattern.SchemaName), RetentionKind.Structural),
        Rule<McpLearnedPattern>(nameof(McpLearnedPattern.TableName), RetentionKind.Structural),
        Rule<McpLearnedPattern>(nameof(McpLearnedPattern.ColumnName), RetentionKind.Structural),
        Rule<McpLearnedPattern>(nameof(McpLearnedPattern.PatternContent), RetentionKind.Structural),
        Rule<McpLearnedPattern>(nameof(McpLearnedPattern.ExampleQuestion), RetentionKind.Content),
        Rule<McpLearnedPattern>(nameof(McpLearnedPattern.ExampleSql), RetentionKind.Content),

        // McpDocumentationPatch — schema documentation derived from PatternContent, not from interaction text.
        Rule<McpDocumentationPatch>(nameof(McpDocumentationPatch.TargetIdentifier), RetentionKind.Structural),
        Rule<McpDocumentationPatch>(nameof(McpDocumentationPatch.CurrentContent), RetentionKind.Structural),
        Rule<McpDocumentationPatch>(nameof(McpDocumentationPatch.ProposedContent), RetentionKind.Structural),
        Rule<McpDocumentationPatch>(nameof(McpDocumentationPatch.Reasoning), RetentionKind.Structural),

        // McpEvalCase — a golden case copies the question and its SQL; promotion is blocked under the lock.
        Rule<McpEvalCase>(nameof(McpEvalCase.Question), RetentionKind.Content),
        Rule<McpEvalCase>(nameof(McpEvalCase.GoldSql), RetentionKind.Content),
        Rule<McpEvalCase>(nameof(McpEvalCase.GoldResultFingerprint), RetentionKind.Structural),
        Rule<McpEvalCase>(nameof(McpEvalCase.Notes), RetentionKind.Content),

        // McpEvalResult has no ProjectId: the belt can only apply the global decision; per-project redaction
        // happens in McpEvalService, which knows the run's project.
        Rule<McpEvalResult>(nameof(McpEvalResult.GeneratedSql), RetentionKind.Content),
        Rule<McpEvalResult>(nameof(McpEvalResult.ExecutionError), RetentionKind.ErrorClass),
        Rule<McpEvalResult>(nameof(McpEvalResult.JudgeVerdict), RetentionKind.Content),

        Rule<McpEvalRun>(nameof(McpEvalRun.Status), RetentionKind.Structural),
        Rule<McpEvalRun>(nameof(McpEvalRun.Notes), RetentionKind.Structural),

        // Admin-authored business glossary.
        Rule<McpGlossaryTerm>(nameof(McpGlossaryTerm.Term), RetentionKind.Structural),
        Rule<McpGlossaryTerm>(nameof(McpGlossaryTerm.Synonyms), RetentionKind.Structural),
        Rule<McpGlossaryTerm>(nameof(McpGlossaryTerm.Definition), RetentionKind.Structural),
        Rule<McpGlossaryTerm>(nameof(McpGlossaryTerm.TargetSchema), RetentionKind.Structural),
        Rule<McpGlossaryTerm>(nameof(McpGlossaryTerm.TargetTable), RetentionKind.Structural),
        Rule<McpGlossaryTerm>(nameof(McpGlossaryTerm.TargetColumn), RetentionKind.Structural),
        Rule<McpGlossaryTerm>(nameof(McpGlossaryTerm.MetricExpression), RetentionKind.Structural),

        // Project documentation, not interaction content.
        Rule<McpDocChunk>(nameof(McpDocChunk.ChunkText), RetentionKind.Structural),
        Rule<McpDocChunk>(nameof(McpDocChunk.ContextualBlurb), RetentionKind.Structural),

        Rule<McpEmbedding>(nameof(McpEmbedding.Model), RetentionKind.Structural),

        Rule<McpSession>(nameof(McpSession.SessionId), RetentionKind.Structural),

        // Admin configuration: prompts, tool descriptions, PII patterns.
        Rule<McpSettings>(nameof(McpSettings.AskSystemPrompt), RetentionKind.Structural),
        Rule<McpSettings>(nameof(McpSettings.GlobalInstruction), RetentionKind.Structural),
        Rule<McpSettings>(nameof(McpSettings.GetContextDescription), RetentionKind.Structural),
        Rule<McpSettings>(nameof(McpSettings.SearchDescription), RetentionKind.Structural),
        Rule<McpSettings>(nameof(McpSettings.QueryDescription), RetentionKind.Structural),
        Rule<McpSettings>(nameof(McpSettings.GetDocumentationDescription), RetentionKind.Structural),
        Rule<McpSettings>(nameof(McpSettings.AskDescription), RetentionKind.Structural),
        Rule<McpSettings>(nameof(McpSettings.CustomPiiPatterns), RetentionKind.Structural),

        Rule<McpProjectSettings>(nameof(McpProjectSettings.CustomPiiPatterns), RetentionKind.Structural)
    ];

    /// <summary>Content properties declared <c>required</c>: they get <c>""</c> instead of <c>null</c>.</summary>
    public static IReadOnlySet<string> RequiredStringProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Key(typeof(McpQuerySignal), nameof(McpQuerySignal.Question)),
        Key(typeof(McpEvalCase), nameof(McpEvalCase.Question)),
        Key(typeof(McpEvalCase), nameof(McpEvalCase.GoldSql))
    };

    private static readonly Dictionary<Type, Func<object, int?>> ProjectIdSelectors = new()
    {
        [typeof(McpQuerySignal)] = x => ((McpQuerySignal)x).ProjectId,
        [typeof(McpAuditLog)] = x => ((McpAuditLog)x).ProjectId,
        [typeof(McpLearnedPattern)] = x => ((McpLearnedPattern)x).ProjectId,
        [typeof(McpDocumentationPatch)] = x => ((McpDocumentationPatch)x).ProjectId,
        [typeof(McpEvalCase)] = x => ((McpEvalCase)x).ProjectId,
        [typeof(McpEvalRun)] = x => ((McpEvalRun)x).ProjectId,
        [typeof(McpGlossaryTerm)] = x => ((McpGlossaryTerm)x).ProjectId,
        [typeof(McpDocChunk)] = x => ((McpDocChunk)x).ProjectId,
        [typeof(McpEmbedding)] = x => ((McpEmbedding)x).ProjectId,
        [typeof(McpProjectSettings)] = x => ((McpProjectSettings)x).ProjectId
    };

    private static readonly Dictionary<Type, McpRetentionRule[]> RulesByEntity = Rules
        .GroupBy(x => x.Entity)
        .ToDictionary(x => x.Key, x => x.ToArray());

    /// <summary>The rules for an entity type, or an empty list when the type is not registered.</summary>
    public static IReadOnlyList<McpRetentionRule> RulesFor(Type entity) =>
        RulesByEntity.TryGetValue(entity, out var rules) ? rules : [];

    /// <summary>The project-id accessor for an entity type, or <c>null</c> when the entity carries no project.</summary>
    public static Func<object, int?>? ProjectIdSelector(Type entity) =>
        ProjectIdSelectors.TryGetValue(entity, out var selector) ? selector : null;

    /// <summary>True when the property must be set to <c>""</c> rather than <c>null</c>.</summary>
    public static bool IsRequiredString(Type entity, string property) =>
        RequiredStringProperties.Contains(Key(entity, property));

    /// <summary>Every writable public string property of an entity — the set the registry must partition.</summary>
    public static IReadOnlyList<PropertyInfo> StringProperties(Type entity) =>
        entity.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.PropertyType == typeof(string))
            .Where(x => x.CanRead)
            .Where(x => x.CanWrite)
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The <c>Entity.Property</c> keys the registry does not classify. Empty is the only acceptable result for the
    /// real entity set (SC2); the test also feeds a synthetic type through it to prove the check bites.
    /// </summary>
    public static IReadOnlyList<string> UnclassifiedProperties(IEnumerable<Type> entities)
    {
        var classified = new HashSet<string>(Rules.Select(x => Key(x.Entity, x.Property)), StringComparer.Ordinal);
        var unclassified = new List<string>();

        foreach (var entity in entities)
        {
            foreach (var property in StringProperties(entity))
            {
                if (!classified.Contains(Key(entity, property.Name)))
                {
                    unclassified.Add(Key(entity, property.Name));
                }
            }
        }

        return unclassified;
    }

    private static McpRetentionRule Rule<TEntity>(string property, RetentionKind kind, Func<string, bool>? alreadyRedacted = null) =>
        new(typeof(TEntity), property, kind, alreadyRedacted);

    private static string Key(Type entity, string property) => $"{entity.Name}.{property}";
}
