using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.HostData;
using Beacon.Core.Services.Security;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// Composes the existing validators into one ordered gate (§1.5 defense in depth). Stage order and the
/// fail-closed rules are load-bearing: the length cap runs first (so no regex or parser ever sees an
/// over-long input), the refusal of control characters and Unicode noncharacters second (the parser reads U+FFFF as the end
/// of its input), the regex guardrail third, the AST validator fourth, and a read-only failure
/// short-circuits every later stage so no schema, lint or row-limit work is ever done on a statement that
/// may not run. A name in another database than the target's configured one, and a host policy violation, then block
/// whatever the read-only and schema settings. Stateless — the validators keep their own identifier-only logging.
/// </summary>
public sealed class SqlExecutionGate(
    IQueryGuardrailService guardrail,
    SqlReadOnlyAstValidator readOnlyAstValidator,
    SqlSchemaValidator schemaValidator,
    SqlSemanticLinter semanticLinter,
    ILogger<SqlExecutionGate> logger,
    IHostDataSourceGuard? hostGuard = null,
    IOptions<McpDeploymentOptions>? deploymentOptions = null) : ISqlExecutionGate
{
    private const string NotRequested = SqlGateCodes.NotRequested;
    private const string NotEvaluated = SqlGateCodes.NotEvaluated;

    public SqlGateReport Evaluate(SqlGateRequest request)
    {
        // The deployment's read-only lock (Beacon:Mcp:ForceReadOnly, on unless the host turns it off) wins over the
        // caller: read-only enforcement is relaxed only where the deployment allows it — for the in-memory join and the
        // ask / query / dry_run tools alike, whatever the settings a caller passes say.
        if (!request.EnforceReadOnly && (deploymentOptions?.Value ?? new McpDeploymentOptions()).ForceReadOnly)
        {
            request = request with { EnforceReadOnly = true, ReadOnlyPinnedByLock = true };
        }

        if (IsTooLong(request.Sql, out var maxSqlChars))
        {
            return RejectBeforeParsing(
                request,
                SqlGateCodes.SqlTooLong,
                $"SQL is {request.Sql.Length} characters long; the limit is {maxSqlChars}.");
        }

        // Before any regex or parser sees the text: the parser reads U+FFFF as the end of its input, so every later stage
        // would judge only the part before it. Refused whatever the read-only setting, the in-memory join included.
        var disallowed = SqlAst.IndexOfDisallowedCharacter(request.Sql);
        if (disallowed >= 0)
        {
            return RejectBeforeParsing(request, SqlGateCodes.DisallowedCharacter, SqlAst.DisallowedCharacterMessageAt(disallowed));
        }

        var validation = guardrail.ValidateQuery(request.Sql, new QueryGuardrailOptions
        {
            ReadOnly = request.EnforceReadOnly,
            DetectPii = request.DetectPii,
            CustomPiiPatterns = request.CustomPiiPatterns?.ToList(),
            Dialect = request.Dialect
        });

        var piiColumns = (IReadOnlyList<string>?)validation.PiiColumns ?? [];
        var readOnly = NameTheLockIfItCausedTheRefusal(request, EvaluateReadOnly(request, validation));

        if (readOnly.Status == SqlGateStatus.Fail)
        {
            return new SqlGateReport(
                true,
                readOnly.Message,
                request.Sql,
                [],
                [],
                piiColumns,
                [],
                new SqlGateVerdicts(
                    readOnly,
                    SqlGateVerdict.Skipped(NotEvaluated),
                    SqlGateVerdict.Skipped(NotEvaluated),
                    SqlGateVerdict.Skipped(NotEvaluated)));
        }

        // The schema walk always runs so TablesUsed / ColumnsUsed are available even when no catalog check
        // was requested; the verdict alone reflects whether a catalog was supplied.
        var schemaResult = schemaValidator.Validate(request.Sql, ToCatalog(request.Catalog), request.Dialect);

        // A name in another database, or a function or path that reaches another server, file or storage, leaves the data
        // source's boundary: authorization, not advice — it always blocks, also with read-only enforcement off (SQL that
        // cannot be parsed cannot be checked, so it blocks too). Only the in-memory join, which reaches nothing else, is
        // not checked.
        var crossDatabaseError = request.InMemoryJoin
            ? null
            : SqlCrossDatabaseRule.Validate(request.Sql, request.Dialect, request.ConfiguredDatabase);
        if (crossDatabaseError != null)
        {
            return new SqlGateReport(
                true,
                crossDatabaseError,
                request.Sql,
                schemaResult.TablesUsed,
                schemaResult.ColumnsUsed,
                piiColumns,
                [],
                new SqlGateVerdicts(
                    readOnly,
                    SqlGateVerdict.Fail(SqlGateCodes.CrossDatabase, crossDatabaseError),
                    SqlGateVerdict.Skipped(NotEvaluated),
                    SqlGateVerdict.Skipped(NotEvaluated)));
        }

        // Host-exposed DbContext: the allow-list is authorization, not advice — a violation always blocks.
        var hostCheck = EvaluateHostPolicy(request);
        if (hostCheck is { Allowed: false })
        {
            return new SqlGateReport(
                true,
                hostCheck.Error,
                request.Sql,
                schemaResult.TablesUsed,
                schemaResult.ColumnsUsed,
                piiColumns,
                [],
                new SqlGateVerdicts(
                    readOnly,
                    SqlGateVerdict.Fail(SqlGateCodes.HostPolicy, hostCheck.Error ?? "Host data source policy violation."),
                    SqlGateVerdict.Skipped(NotEvaluated),
                    SqlGateVerdict.Skipped(NotEvaluated)));
        }

        if (hostCheck is { MaskedOutputColumns.Count: > 0 })
        {
            piiColumns = piiColumns
                .Concat(hostCheck.MaskedOutputColumns)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var schema = EvaluateSchema(request, schemaResult);

        var blocked = request.BlockOnSchemaFailure && schema.Status == SqlGateStatus.Fail;
        var blockReason = blocked ? schema.Message : null;

        var (lint, lintFindings) = EvaluateLint(request, blocked);
        var (rowLimit, finalSql) = EvaluateRowLimit(request, blocked);

        // A statement too deep to cap, or whose own bound cannot be capped, never runs uncapped, even with read-only
        // enforcement off.
        if (rowLimit.Status == SqlGateStatus.Fail)
        {
            blocked = true;
            blockReason = rowLimit.Message;
        }

        return new SqlGateReport(
            blocked,
            blockReason,
            finalSql,
            schemaResult.TablesUsed,
            schemaResult.ColumnsUsed,
            piiColumns,
            lintFindings,
            new SqlGateVerdicts(readOnly, schema, lint, rowLimit))
        {
            HostMaskedColumns = hostCheck?.MaskedOutputColumns ?? []
        };
    }

    private bool IsTooLong(string? sql, out int maxSqlChars)
    {
        maxSqlChars = (deploymentOptions?.Value.Ceilings ?? new McpCeilingOptions()).EffectiveMaxSqlChars;

        return (sql?.Length ?? 0) > maxSqlChars;
    }

    // Read-only verdict, so every caller's existing "read-only failed → blocked, nothing ran" handling applies.
    // The reason carries counts and positions only, never SQL text (§1.11).
    private static SqlGateReport RejectBeforeParsing(SqlGateRequest request, string code, string message)
    {
        return new SqlGateReport(
            true,
            message,
            request.Sql,
            [],
            [],
            [],
            [],
            new SqlGateVerdicts(
                SqlGateVerdict.Fail(code, message),
                SqlGateVerdict.Skipped(NotEvaluated),
                SqlGateVerdict.Skipped(NotEvaluated),
                SqlGateVerdict.Skipped(NotEvaluated)));
    }

    private HostPolicyResult? EvaluateHostPolicy(SqlGateRequest request)
    {
        if (request.HostManagedKey == null)
        {
            return null;
        }

        // Fail closed: a host-managed target without the guard wired cannot be checked, so it cannot run.
        return hostGuard?.Check(request.HostManagedKey, request.Sql)
            ?? HostPolicyResult.Reject("Host data source policy is unavailable; the query was not run.");
    }

    private SqlGateVerdict EvaluateReadOnly(SqlGateRequest request, QueryValidationResult validation)
    {
        if (!validation.IsValid)
        {
            var code = string.IsNullOrWhiteSpace(request.Sql) ? SqlGateCodes.Empty : SqlGateCodes.Guardrail;

            return SqlGateVerdict.Fail(code, validation.Error ?? "Query validation failed.");
        }

        if (!request.EnforceReadOnly)
        {
            return SqlGateVerdict.Skipped(SqlGateCodes.Disabled);
        }

        var astError = readOnlyAstValidator.Validate(request.Sql, request.Dialect);

        return astError == null
            ? SqlGateVerdict.Passed
            : SqlGateVerdict.Fail(SqlGateCodes.Ast, astError);
    }

    // A refusal that only read-only enforcement causes, while the deployment lock turned it on over stored settings that
    // had it off, says so: the caller's settings page shows read-only off, and the lock is what to look at.
    private SqlGateVerdict NameTheLockIfItCausedTheRefusal(SqlGateRequest request, SqlGateVerdict readOnly)
    {
        if (readOnly.Status != SqlGateStatus.Fail || !request.ReadOnlyPinnedByLock)
        {
            return readOnly;
        }

        var causedByReadOnly = readOnly.Code == SqlGateCodes.Ast
            || (readOnly.Code == SqlGateCodes.Guardrail
                && guardrail.ValidateQuery(request.Sql, new QueryGuardrailOptions { ReadOnly = false, Dialect = request.Dialect }).IsValid);

        return causedByReadOnly
            ? readOnly with
            {
                Message = $"{readOnly.Message} Read-only enforcement is on because the deployment lock {McpDeploymentOptions.SectionName}:{nameof(McpDeploymentOptions.ForceReadOnly)} pins it, whatever the stored MCP settings say."
            }
            : readOnly;
    }

    private static SqlGateVerdict EvaluateSchema(SqlGateRequest request, SqlValidationResult result)
    {
        if (request.Catalog == null)
        {
            return SqlGateVerdict.Skipped(NotRequested);
        }

        if (!result.Checked)
        {
            return SqlGateVerdict.Skipped(
                SqlGateCodes.EmptyCatalog,
                "No schema metadata available for this data source yet — column check was skipped.");
        }

        return result.IsValid
            ? SqlGateVerdict.Passed
            : SqlGateVerdict.Fail(SqlGateCodes.Schema, result.Error ?? "Schema validation failed.");
    }

    private (SqlGateVerdict Verdict, IReadOnlyList<SqlLintFinding> Findings) EvaluateLint(SqlGateRequest request, bool blocked)
    {
        if (request.LintContext == null)
        {
            return (SqlGateVerdict.Skipped(NotRequested), []);
        }

        if (!request.EnableSemanticLint)
        {
            return (SqlGateVerdict.Skipped(SqlGateCodes.Disabled), []);
        }

        if (blocked)
        {
            return (SqlGateVerdict.Skipped(NotEvaluated), []);
        }

        if (!semanticLinter.TryLint(request.Sql, request.Dialect, request.LintContext, out var findings))
        {
            return (SqlGateVerdict.Skipped(SqlGateCodes.ParseFailed, "The SQL could not be parsed, so the semantic lint did not run."), []);
        }

        return (SqlGateVerdict.Passed, findings);
    }

    private (SqlGateVerdict Verdict, string FinalSql) EvaluateRowLimit(SqlGateRequest request, bool blocked)
    {
        if (request.MaxRows == null)
        {
            return (SqlGateVerdict.Skipped(NotRequested), request.Sql);
        }

        if (blocked)
        {
            return (SqlGateVerdict.Skipped(NotEvaluated), request.Sql);
        }

        var result = SqlRowLimitRewriter.Apply(request.Sql, request.MaxRows.Value, request.Dialect);
        if (result.Outcome == SqlRowLimitOutcome.TextualFallback)
        {
            // Reachable when EnforceReadOnly is off (the AST gate rejects unparseable SQL otherwise); the rewriter
            // falls back to text only when the parser throws.
            // The textual heuristic is the pre-gate behaviour and cannot see inside comments or literals,
            // so say so on the verdict and leave an identifier-only trace (§1.11) — never the SQL text.
            logger.LogWarning(
                "Row limit applied textually: SQL did not parse for dialect {Dialect} ({ParseError}); read-only enforcement is off for this request.",
                request.Dialect,
                result.FallbackReason);
        }

        var verdict = result.Outcome switch
        {
            SqlRowLimitOutcome.Applied => SqlGateVerdict.Passed,
            SqlRowLimitOutcome.AlreadyLimited => SqlGateVerdict.Skipped(SqlGateCodes.AlreadyLimited),
            SqlRowLimitOutcome.OwnBoundCapped => SqlGateVerdict.Pass(SqlGateCodes.OwnBoundCapped),
            SqlRowLimitOutcome.NotApplicable => SqlGateVerdict.Skipped(SqlGateCodes.NotApplicable),
            SqlRowLimitOutcome.Refused => SqlGateVerdict.Fail(
                result.RefusalCode ?? SqlGateCodes.TooDeep,
                result.RefusalMessage ?? SqlAst.TooDeepMessage),
            _ => SqlGateVerdict.Pass(
                SqlGateCodes.TextualFallback,
                "The SQL could not be parsed, so the row cap was placed by the textual heuristic; a LIMIT/TOP inside a comment or literal may have been mistaken for an existing bound.")
        };

        return (verdict, result.Sql);
    }

    private static Dictionary<string, HashSet<string>> ToCatalog(IReadOnlyDictionary<string, HashSet<string>>? catalog)
    {
        if (catalog == null)
        {
            return [];
        }

        return catalog as Dictionary<string, HashSet<string>>
            ?? new Dictionary<string, HashSet<string>>(catalog, StringComparer.OrdinalIgnoreCase);
    }
}
