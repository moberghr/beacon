using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services;

/// <summary>
/// Extracts a structured, MAGIC-style lesson from a single schema-correction failure cluster using an
/// LLM. The interface lives in Core (mirroring <see cref="IMcpLearningAggregationService"/> /
/// <see cref="IMcpEvalService"/>); the implementation lives in Beacon.AI and is wired at the composition
/// root. It is LLM-PRIMARY: the aggregation service calls it first and only falls back to the
/// deterministic regex + <c>ColumnSimilarity</c> path when no provider is configured or the call
/// fails/parses badly (<see cref="ExtractAsync"/> returns <c>null</c>). Only the cluster's text fields
/// are sent to the LLM — never bulk result rows (§1.11).
/// </summary>
public interface ILessonExtractor
{
    /// <summary>
    /// True when the extractor can be attempted. The delegating provider is always injected via DI, so
    /// this is normally true; a failed/misconfigured provider surfaces as a <c>null</c> result from
    /// <see cref="ExtractAsync"/> (the caller then falls back), not as unavailability.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Reads the failure cluster and returns a structured lesson, or <c>null</c> when the provider is
    /// unavailable, the call fails, or the output cannot be parsed into a usable lesson. Never throws for
    /// provider/parse errors (cancellation is propagated).
    /// </summary>
    Task<ExtractedLesson?> ExtractAsync(FailureCluster cluster, CancellationToken ct);
}

/// <summary>
/// The bounded, text-only context handed to the LLM for one correction cluster. Carries no bulk result
/// rows (§1.11) — only the question, the failing/corrected SQL, the error text, and an optional schema
/// slice.
/// </summary>
public sealed record FailureCluster(
    int DataSourceId,
    string SchemaName,
    string TableName,
    string? ColumnName,
    string Question,
    string? GeneratedSql,
    string? Error,
    string? CorrectedSql,
    string? SchemaSlice);

/// <summary>
/// The structured lesson emitted by the LLM. <see cref="PatternContent"/> / <see cref="ExampleQuestion"/>
/// / <see cref="ExampleSql"/> / <see cref="PatternType"/> feed straight into <c>UpsertPatternAsync</c>;
/// the MAGIC-style <see cref="Symptom"/> / <see cref="RootCause"/> / <see cref="Rule"/> /
/// <see cref="ExampleFix"/> / <see cref="ApplicableWhen"/> capture the diagnostic reasoning.
/// </summary>
public sealed record ExtractedLesson(
    McpPatternType PatternType,
    string PatternContent,
    string? ExampleQuestion,
    string? ExampleSql,
    string Symptom,
    string RootCause,
    string Rule,
    string? ExampleFix,
    string? ApplicableWhen);
