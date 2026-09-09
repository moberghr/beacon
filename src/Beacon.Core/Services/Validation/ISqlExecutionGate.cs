using Beacon.Core.Models;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// The single pre-execution gate every MCP SQL path runs before SQL reaches a connector: regex guardrail →
/// AST read-only validator → schema catalog check → semantic lint → row-limit rewrite. Pure and synchronous
/// (no I/O) so it can be evaluated on every candidate, including every repair attempt, at no cost beyond a
/// parse. Provider-side checks (dry-run / EXPLAIN) deliberately live outside it.
/// </summary>
public interface ISqlExecutionGate
{
    SqlGateReport Evaluate(SqlGateRequest request);
}

/// <summary>
/// One gate evaluation. A null <paramref name="Catalog"/>, <paramref name="LintContext"/> or
/// <paramref name="MaxRows"/> means "this stage was not requested" and yields a <c>Skipped("not_requested")</c>
/// verdict rather than a pass.
/// </summary>
/// <param name="Dialect">Database engine name (<c>DatabaseEngineType.ToString()</c>) or <c>"SQLite"</c>.</param>
/// <param name="BlockOnSchemaFailure">When true a schema-gate failure blocks execution (the <c>query</c> tool);
/// when false it is advisory and left to the caller's repair loop (<c>ask</c>, <c>dry_run</c>, cross-source).</param>
public sealed record SqlGateRequest(
    string Sql,
    string? Dialect,
    bool EnforceReadOnly,
    bool DetectPii,
    IReadOnlyList<string>? CustomPiiPatterns,
    IReadOnlyDictionary<string, HashSet<string>>? Catalog,
    bool BlockOnSchemaFailure,
    SchemaLintContext? LintContext,
    bool EnableSemanticLint,
    int? MaxRows)
{
    /// <summary>
    /// Read-only / PII / lint flags copied from the effective MCP settings; catalog, lint context and row limit
    /// left unset so a caller adds only the stages it needs via a <c>with</c> expression.
    /// </summary>
    public static SqlGateRequest FromSettings(string sql, string? dialect, McpSettingsData settings)
    {
        return new SqlGateRequest(
            sql,
            dialect,
            settings.EnforceReadOnly,
            settings.EnablePiiDetection,
            settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null,
            Catalog: null,
            BlockOnSchemaFailure: false,
            LintContext: null,
            settings.EnableSemanticLint,
            MaxRows: null);
    }
}

public enum SqlGateStatus
{
    Pass,
    Fail,
    Skipped
}

/// <summary>
/// Stable machine-readable verdict codes. Consumers (tools, telemetry, future gates) reference these
/// constants rather than string literals so a rename cannot silently desync a caller.
/// </summary>
public static class SqlGateCodes
{
    /// <summary>The stage was not asked for (null catalog / lint context / row limit).</summary>
    public const string NotRequested = "not_requested";

    /// <summary>An earlier stage blocked the SQL, so this stage never ran.</summary>
    public const string NotEvaluated = "not_evaluated";

    /// <summary>The stage is switched off by settings.</summary>
    public const string Disabled = "disabled";

    /// <summary>Read-only: blank SQL.</summary>
    public const string Empty = "empty";

    /// <summary>Read-only: the regex guardrail rejected the SQL.</summary>
    public const string Guardrail = "guardrail";

    /// <summary>Read-only: the AST validator rejected the SQL (or could not parse it).</summary>
    public const string Ast = "ast";

    /// <summary>Schema: the catalog check failed.</summary>
    public const string Schema = "schema";

    /// <summary>Schema: no catalog rows exist for the data source, so nothing could be checked.</summary>
    public const string EmptyCatalog = "empty_catalog";

    /// <summary>Row limit: the outermost query already bounds its result.</summary>
    public const string AlreadyLimited = "already_limited";

    /// <summary>Row limit: the statement carries no result set to cap.</summary>
    public const string NotApplicable = "not_applicable";

    /// <summary>Row limit: the SQL did not parse, so the legacy textual heuristic decided the placement.</summary>
    public const string TextualFallback = "textual_fallback";
}

/// <summary>One stage's verdict. <see cref="Code"/> is a stable machine-readable token; <see cref="Message"/> is caller-facing text.</summary>
public sealed record SqlGateVerdict(SqlGateStatus Status, string? Code, string? Message)
{
    public static readonly SqlGateVerdict Passed = new(SqlGateStatus.Pass, null, null);

    public static SqlGateVerdict Pass(string code, string? message = null) => new(SqlGateStatus.Pass, code, message);

    public static SqlGateVerdict Skipped(string code, string? message = null) => new(SqlGateStatus.Skipped, code, message);

    public static SqlGateVerdict Fail(string code, string message) => new(SqlGateStatus.Fail, code, message);
}

public sealed record SqlGateVerdicts(SqlGateVerdict ReadOnly, SqlGateVerdict Schema, SqlGateVerdict Lint, SqlGateVerdict RowLimit);

/// <summary>
/// Everything a caller needs from one evaluation. <see cref="FinalSql"/> is the SQL to execute (row-limited
/// when <c>MaxRows</c> was requested, otherwise the input unchanged); <see cref="TablesUsed"/> and
/// <see cref="ColumnsUsed"/> come from the AST walk and are populated whenever the SQL parses.
/// </summary>
public sealed record SqlGateReport(
    bool Blocked,
    string? BlockReason,
    string FinalSql,
    IReadOnlyList<string> TablesUsed,
    IReadOnlyList<string> ColumnsUsed,
    IReadOnlyList<string> PiiColumns,
    IReadOnlyList<SqlLintFinding> LintFindings,
    SqlGateVerdicts Verdicts);
