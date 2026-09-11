using Microsoft.Extensions.Logging;
using Beacon.Core.Services.Security;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// Composes the existing validators into one ordered gate (§1.5 defense in depth). Stage order and the
/// fail-closed rules are load-bearing: the regex guardrail runs first, the AST validator second, and a
/// read-only failure short-circuits every later stage so no schema, lint or row-limit work is ever done
/// on a statement that may not run. Stateless — the validators keep their own identifier-only logging.
/// </summary>
public sealed class SqlExecutionGate(
    IQueryGuardrailService guardrail,
    SqlReadOnlyAstValidator readOnlyAstValidator,
    SqlSchemaValidator schemaValidator,
    SqlSemanticLinter semanticLinter,
    ILogger<SqlExecutionGate> logger) : ISqlExecutionGate
{
    private const string NotRequested = SqlGateCodes.NotRequested;
    private const string NotEvaluated = SqlGateCodes.NotEvaluated;

    public SqlGateReport Evaluate(SqlGateRequest request)
    {
        var validation = guardrail.ValidateQuery(request.Sql, new QueryGuardrailOptions
        {
            ReadOnly = request.EnforceReadOnly,
            DetectPii = request.DetectPii,
            CustomPiiPatterns = request.CustomPiiPatterns?.ToList()
        });

        var piiColumns = (IReadOnlyList<string>?)validation.PiiColumns ?? [];
        var readOnly = EvaluateReadOnly(request, validation);

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
        var schema = EvaluateSchema(request, schemaResult);

        var blocked = request.BlockOnSchemaFailure && schema.Status == SqlGateStatus.Fail;
        var blockReason = blocked ? schema.Message : null;

        var (lint, lintFindings) = EvaluateLint(request, blocked);
        var (rowLimit, finalSql) = EvaluateRowLimit(request, blocked);

        return new SqlGateReport(
            blocked,
            blockReason,
            finalSql,
            schemaResult.TablesUsed,
            schemaResult.ColumnsUsed,
            piiColumns,
            lintFindings,
            new SqlGateVerdicts(readOnly, schema, lint, rowLimit));
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

        var findings = semanticLinter.Lint(request.Sql, request.Dialect, request.LintContext);

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
            // Reachable only when EnforceReadOnly is off (the AST gate rejects unparseable SQL otherwise).
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
            SqlRowLimitOutcome.NotApplicable => SqlGateVerdict.Skipped(SqlGateCodes.NotApplicable),
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
