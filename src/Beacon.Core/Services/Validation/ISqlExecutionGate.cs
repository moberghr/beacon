using Beacon.Core.Models;
using Beacon.Core.RowSecurity;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// The single pre-execution gate every MCP SQL path runs before SQL reaches a connector: length cap → regex guardrail →
/// AST read-only validator → cross-database names → system catalog relations → row-level security → host policy → schema
/// catalog check → semantic lint → row-limit rewrite. Pure and synchronous (no I/O) so it can be evaluated on every
/// candidate, including every repair attempt, at no cost beyond a parse. Provider-side checks (dry-run / EXPLAIN) deliberately live outside it.
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
/// <param name="Dialect">The target's dialect name from <see cref="DataSourceSqlDialect.Of(Beacon.Core.Data.Entities.DataSource)"/>, or
/// <c>"SQLite"</c> for the in-memory join (<see cref="ForInMemoryJoin"/>). An unknown or null dialect fails the read-only stage: SQL is never judged under a
/// generic grammar.</param>
/// <param name="BlockOnSchemaFailure">When true a schema-gate failure blocks execution (the <c>query</c> tool);
/// when false it is advisory and left to the caller's repair loop (<c>ask</c>, <c>dry_run</c>, cross-source).</param>
/// <param name="RowSecurity">Row-level security for the caller on the target, from <see cref="IRowSecurityResolver"/>: a
/// <see cref="RowSecurityState.Denied"/> or <see cref="RowSecurityState.Filtered"/> context blocks whatever the other
/// settings say (<see cref="SqlGateCodes.NoEntitlement"/>, <see cref="SqlGateCodes.RowSecurityUnrewritable"/>), and so
/// does a missing one.</param>
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
    int? MaxRows,
    RowSecurityContext RowSecurity)
{
    /// <summary>
    /// <c>DataSource.HostManagedKey</c> of the target when it is a host-exposed DbContext. When set, the host
    /// allow-list/exclusion policy runs and a violation BLOCKS regardless of <see cref="BlockOnSchemaFailure"/>.
    /// </summary>
    public string? HostManagedKey { get; init; }

    /// <summary>
    /// The database the target is configured for — its catalog on Databricks, its project on BigQuery
    /// (<see cref="IConfiguredDatabaseResolver"/>). A name the SQL qualifies with any other database BLOCKS, regardless of
    /// <see cref="EnforceReadOnly"/> and <see cref="BlockOnSchemaFailure"/>; null allows no database qualifier at all
    /// (<see cref="SqlCrossDatabaseRule"/>), and so does <see cref="DataSourceConfiguredDatabase.Undetermined"/>.
    /// </summary>
    public string? ConfiguredDatabase { get; init; }

    /// <summary>
    /// True only for the statement that joins step results on Beacon's own in-memory SQLite database
    /// (<see cref="ForInMemoryJoin"/>): it reaches no other database, so neither <see cref="SqlCrossDatabaseRule"/> nor
    /// <see cref="SqlSystemCatalogRule"/> applies.
    /// A SQLite data source is checked like any other.
    /// </summary>
    public bool InMemoryJoin { get; init; }

    /// <summary>
    /// True when the deployment lock <c>Beacon:Mcp:ForceReadOnly</c> turned read-only enforcement on over stored settings
    /// that had it off (<see cref="McpSettingsData.ReadOnlyPinnedByLock"/>): a refusal only read-only enforcement causes then
    /// names the lock.
    /// </summary>
    internal bool ReadOnlyPinnedByLock { get; init; }

    /// <summary>
    /// Read-only / PII / lint flags copied from the effective MCP settings; catalog, lint context and row limit
    /// left unset so a caller adds only the stages it needs via a <c>with</c> expression.
    /// </summary>
    public static SqlGateRequest FromSettings(string sql, string? dialect, McpSettingsData settings, RowSecurityContext rowSecurity)
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
            MaxRows: null,
            rowSecurity)
        {
            ReadOnlyPinnedByLock = settings.ReadOnlyPinnedByLock
        };
    }

    /// <summary>
    /// <see cref="FromSettings"/> for the statement that joins step results on the in-memory SQLite database: the SQLite
    /// dialect, no cross-database check (<see cref="InMemoryJoin"/>), and no row filter — it reads only the step results,
    /// each resolved for its own data source.
    /// </summary>
    public static SqlGateRequest ForInMemoryJoin(string sql, McpSettingsData settings)
    {
        return FromSettings(sql, "SQLite", settings, RowSecurityContext.NotApplicable(RowSecurityContext.InMemoryJoinReason)) with { InMemoryJoin = true };
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

    /// <summary>Read-only: the SQL is longer than <c>Beacon:Mcp:Ceilings:MaxSqlChars</c>, so no other stage ran.</summary>
    public const string SqlTooLong = "sql_too_long";

    /// <summary>
    /// Read-only: the SQL holds a control character other than tab, LF and CR, or a Unicode noncharacter, so no other stage
    /// ran.
    /// </summary>
    public const string DisallowedCharacter = "disallowed_character";

    /// <summary>Read-only: the regex guardrail rejected the SQL.</summary>
    public const string Guardrail = "guardrail";

    /// <summary>Read-only: the AST validator rejected the SQL (or could not parse it).</summary>
    public const string Ast = "ast";

    /// <summary>Schema: the catalog check failed.</summary>
    public const string Schema = "schema";

    /// <summary>Schema: the SQL reads a table or column the host data source does not expose.</summary>
    public const string HostPolicy = "host_policy";

    /// <summary>Schema: the SQL names another database (catalog, project) than the one the data source is configured for.</summary>
    public const string CrossDatabase = "cross_database";

    /// <summary>Schema: the SQL reads the engine's catalog, statistics or query history (<see cref="SqlSystemCatalogRule"/>).</summary>
    public const string SystemCatalog = "system_catalog";

    /// <summary>Row-level security: the caller may not read the data source's rows (<see cref="RowSecurityState.Denied"/>).</summary>
    public const string NoEntitlement = "no_entitlement";

    /// <summary>
    /// Row-level security: the caller's rows are filtered (<see cref="RowSecurityState.Filtered"/>), and the gate cannot write
    /// the filter into the statement, so it is blocked rather than run unfiltered.
    /// </summary>
    public const string RowSecurityUnrewritable = "row_security_unrewritable";

    /// <summary>Schema: no catalog rows exist for the data source, so nothing could be checked.</summary>
    public const string EmptyCatalog = "empty_catalog";

    /// <summary>Row limit: the outermost query already bounds its result.</summary>
    public const string AlreadyLimited = "already_limited";

    /// <summary>
    /// Row limit: the outermost query's own bound did not keep its result within the cap, so the bound was lowered to the
    /// cap or the statement was wrapped in a capped derived table.
    /// </summary>
    public const string OwnBoundCapped = "own_bound_capped";

    /// <summary>Row limit: the outermost query's own bound can neither be checked against the cap nor capped, so it is blocked.</summary>
    public const string UncappableBound = "uncappable_bound";

    /// <summary>Row limit: the requested cap is zero or below, so the statement is blocked rather than run uncapped.</summary>
    public const string InvalidRowCap = "invalid_row_cap";

    /// <summary>Row limit: the statement carries no result set to cap.</summary>
    public const string NotApplicable = "not_applicable";

    /// <summary>Row limit: the SQL did not parse, so the legacy textual heuristic decided the placement.</summary>
    public const string TextualFallback = "textual_fallback";

    /// <summary>Row limit: the SQL is nested too deeply to cap safely, so it is blocked.</summary>
    public const string TooDeep = "too_deep";

    /// <summary>Lint: the SQL did not parse, so no semantic check ran.</summary>
    public const string ParseFailed = "parse_failed";
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
    SqlGateVerdicts Verdicts)
{
    /// <summary>
    /// The output columns a host data source's policy masks for this statement (also in <see cref="PiiColumns"/>). Their
    /// values are masked in full, never partially: pass them to <c>PiiRowMasker.Mask</c> as its host-masked columns.
    /// </summary>
    public IReadOnlyList<string> HostMaskedColumns { get; init; } = [];
}
