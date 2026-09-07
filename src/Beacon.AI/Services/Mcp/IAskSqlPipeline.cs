using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Models;
using Beacon.Core.Services.Validation;

namespace Beacon.AI.Services.Mcp;

/// <summary>
/// The generate → validate → repair → execute core shared by the MCP <c>ask</c> tool and the eval
/// harness (spec item ①). Both callers run the SAME instance type with the SAME repair budget, so eval
/// accuracy is production accuracy (SC1). The pipeline never touches <c>Beacon.MCP</c> types: it returns
/// a structured <see cref="AskSqlOutcome"/> that <c>ProjectAskTool</c> maps onto its signal builder (R2).
/// </summary>
public interface IAskSqlPipeline
{
    Task<AskSqlOutcome> RunAsync(
        ILlmProvider llmProvider,
        int dataSourceId,
        int projectId,
        string question,
        McpSettingsData settings,
        IAskSqlExecutor executor,
        AskSqlPipelineOptions options,
        CancellationToken ct);
}

/// <summary>
/// Per-call knobs. <paramref name="ExtraContext"/> is the replay gate's candidate-lesson suffix;
/// <paramref name="GenerationTemperature"/> pins sampling (the gate passes 0.0) and
/// <paramref name="AllowSelfConsistency"/> lets a deterministic caller opt out of voting.
/// </summary>
public sealed record AskSqlPipelineOptions(
    bool Execute = true,
    string? ExtraContext = null,
    decimal? GenerationTemperature = null,
    bool AllowSelfConsistency = true);

/// <summary>
/// One repair attempt out of the shared budget. <paramref name="RetriedSql"/> is null when the model
/// returned nothing usable or the retry never cleared validation (nothing was adopted).
/// </summary>
/// <param name="Trigger">One of <c>schema</c>, <c>dry-run</c>, <c>execution</c>, <c>empty-result</c>, <c>lint</c>.</param>
public sealed record AskRepairStep(string Trigger, string Error, string? RetriedSql, bool Succeeded);

/// <summary>
/// Everything a caller needs from one pipeline run. <see cref="Text"/> is the markdown transcript the
/// <c>ask</c> tool renders verbatim; the structured members carry what used to be written straight onto
/// the MCP signal builder.
/// </summary>
public sealed record AskSqlOutcome(
    string GeneratedSql,
    string FinalSql,
    IReadOnlyList<string> TablesUsed,
    bool Succeeded,
    string Text,
    AskExecutionResult? Execution,
    string? ValidationError,
    string? SchemaValidationError,
    string? DryRunError,
    IReadOnlyList<AskRepairStep> Repairs,
    bool EmptyResultRetried,
    string? VotingNote,
    IReadOnlyList<string> Assumptions,
    string? ClarificationHint,
    IReadOnlyList<SqlLintFinding> LintFindings,
    string? CorrectedSql,
    IReadOnlyList<string> ColumnsUsed);
