using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Enums;
using Beacon.Core.Exceptions;
using Beacon.Core.Models;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.RowSecurity;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;

namespace Beacon.Core.Services;

public interface IDataQualityEvaluationService
{
    Task<DataQualityEvaluationData> EvaluateContractAsync(int dataContractId, CancellationToken cancellationToken = default);
    Task<List<DataQualityEvaluationData>> GetEvaluationHistoryAsync(int dataContractId, int take = 20, CancellationToken cancellationToken = default);
}

/// <summary>
/// Evaluates a data contract's rules. Before any rule runs, the evaluation's actor (the caller of a request, or a
/// scheduled evaluation's owner) must still be allowed to run stored SQL on the contract's data source
/// (<see cref="IExecutionAccessCheck"/>); otherwise nothing runs or is recorded. Every rule's SQL passes the read-only
/// gate (<see cref="DataQualityRuleGuard"/>) and runs through <see cref="IDataSourceProvider.ExecuteReadOnlyQueryAsync"/>,
/// which owns the host policy, host column masking, the generic host error and the READ ONLY transaction (§1.5). A failed
/// rule never stores or returns the server's error text: a server or conversion error can quote row values (§1.11). While
/// the change approval workflow covers Custom SQL rules, a Custom SQL rule whose SQL on the contract's data source is not
/// the approved one (<see cref="DataContractRule.ApprovedSqlHash"/>) runs no SQL: each one left out is logged, the
/// evaluation records how many were left out (<see cref="DataQualityEvaluation.AwaitingApprovalRules"/>) and its score
/// is partial — it does not replace the table's score, so it never improves it silently. A contract all of whose enabled
/// rules wait for approval is not evaluated (<see cref="DataContractRulesAwaitApprovalException"/>).
/// </summary>
internal class DataQualityEvaluationService(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataQualitySqlGenerator sqlGenerator,
    IDataSourceProviderFactory providerFactory,
    ISqlExecutionGate gate,
    IConfiguredDatabaseResolver configuredDatabases,
    IExecutionAccessCheck executionAccess,
    IRowSecurityResolver rowSecurityResolver,
    ISharedSourceSettings sharedSourceSettings,
    IQueryGuardrailService guardrail,
    BeaconConfiguration configuration,
    ILogger<DataQualityEvaluationService> logger) : IDataQualityEvaluationService
{
    /// <summary>What a rule result says when its query fails on an ordinary data source.</summary>
    public const string ExecutionFailedMessage = "Execution failed on the data source.";

    /// <summary>What a rule result says when its query runs longer than <see cref="RuleTimeout"/>.</summary>
    public const string TimedOutMessage = "Rule timed out.";

    /// <summary>What a rule result says when its row lacks a column the rule reads, or holds a value of the wrong type.</summary>
    public const string UnexpectedResultMessage = "Rule result did not have the expected columns or types.";

    // The text columns of a CustomSql rule's result, carried into the rule result's value and message.
    private static readonly HashSet<string> CustomSqlTextColumns = new(StringComparer.OrdinalIgnoreCase) { "actual_value", "message" };

    /// <summary>How long one rule's query may run. Settable for tests.</summary>
    internal TimeSpan RuleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public async Task<DataQualityEvaluationData> EvaluateContractAsync(int dataContractId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var contract = await context.DataContracts
            .Include(c => c.Rules)
            .Include(c => c.DataSource)
            .Where(c => c.Id == dataContractId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new Models.BeaconException($"Data contract {dataContractId} not found");

        if (!await executionAccess.AllowsAsync(ProjectAction.Run, [contract.DataSourceId], "data contract", dataContractId, cancellationToken))
        {
            throw new ExecutionRefusedException([contract.DataSourceId], "Evaluating this data contract is not allowed.");
        }

        if (contract.DataSource.DataSourceType != DataSourceType.Database || contract.DataSource.DatabaseEngineType == null)
        {
            throw new Models.BeaconException("Data contract's data source must be a database type");
        }

        var provider = providerFactory.GetProvider(contract.DataSource.DataSourceType);
        var engineType = contract.DataSource.DatabaseEngineType.Value;

        // A Custom SQL rule waiting for approval runs no SQL: it is left out as a disabled rule is.
        var awaitingApproval = configuration.ApprovalWorkflow.Covers(ChangeApprovalKind.CustomSqlRules)
            ? contract.Rules
                .Where(x => x.IsEnabled)
                .Where(x => CustomSqlRuleDefinition.Awaits(x, contract.DataSourceId))
                .ToList()
            : [];
        foreach (var rule in awaitingApproval)
        {
            logger.LogWarning(
                "Custom SQL rule {RuleId} of data contract {ContractId} was not evaluated: its SQL waits for approval",
                rule.Id,
                contract.Id);
        }

        var enabledRules = contract.Rules
            .Where(x => x.IsEnabled)
            .Except(awaitingApproval)
            .ToList();

        // Scoring no rules would report 100, overwrite the real score and keep alerts quiet, so nothing is recorded.
        if (enabledRules.Count == 0 && awaitingApproval.Count > 0)
        {
            throw new DataContractRulesAwaitApprovalException(contract.Id, awaitingApproval.Count);
        }

        if (enabledRules.Count == 0)
        {
            throw new DataContractHasNoEnabledRulesException(contract.Id);
        }

        // Re-checked on every run, so a rule saved before rules were confined to their data source cannot name another.
        var catalog = await DataQualityRuleCatalog.LoadAsync(
            context,
            contract.DataSourceId,
            contract.SchemaName,
            contract.TableName,
            enabledRules.Select(x => x.Configuration),
            cancellationToken);

        // Rule SQL may name the source's own database only (the one its connection is configured for).
        var configuredDatabase = configuredDatabases.Resolve(contract.DataSource);
        var rowSecurity = await rowSecurityResolver.ResolveAsync(contract.DataSourceId, cancellationToken);

        // A rule's result reaches its history and the contract's alerts: masked as the strictest live project that links
        // the data source asks (§1.6).
        var pii = await sharedSourceSettings.GetDataSourceSettingsAsync(contract.DataSourceId, cancellationToken);

        var totalStopwatch = Stopwatch.StartNew();
        var ruleResults = new List<DataQualityRuleResult>();

        foreach (var rule in enabledRules)
        {
            var result = await EvaluateRuleAsync(rule, contract, catalog, provider, engineType, configuredDatabase, rowSecurity, pii, cancellationToken);
            ruleResults.Add(result);
        }

        totalStopwatch.Stop();

        var passedCount = ruleResults.Count(r => r.Passed);
        var failedCount = ruleResults.Count(r => !r.Passed);
        var overallScore = ComputeOverallScore(enabledRules, ruleResults);

        var evaluation = new DataQualityEvaluation
        {
            DataContractId = dataContractId,
            OverallScore = overallScore,
            PassedRules = passedCount,
            FailedRules = failedCount,
            TotalRules = enabledRules.Count,
            AwaitingApprovalRules = awaitingApproval.Count,
            ExecutionTimeMs = totalStopwatch.Elapsed.TotalMilliseconds,
            RuleResults = ruleResults
        };

        // An evaluation ran: a skipped scheduled evaluation recorded earlier no longer describes the contract.
        contract.OwnerNotAuthorizedSince = null;
        context.DataQualityEvaluations.Add(evaluation);
        await context.SaveChangesAsync(cancellationToken);

        // A partial score leaves the table's score as it was: it says nothing about the rules that wait.
        if (awaitingApproval.Count == 0)
        {
            await UpsertScoreAsync(context, contract, overallScore, cancellationToken);
        }

        return MapEvaluation(evaluation, enabledRules);
    }

    public async Task<List<DataQualityEvaluationData>> GetEvaluationHistoryAsync(int dataContractId, int take = 20, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The history keeps the results of rules a later save retired, with the name of the rule that produced them: the
        // soft-delete filter would leave them out.
        var evaluations = await context.DataQualityEvaluations
            .IgnoreQueryFilters()
            .Where(e => e.DataContractId == dataContractId)
            .OrderByDescending(e => e.CreatedTime)
            .Take(take)
            .Select(e => new DataQualityEvaluationData
            {
                Id = e.Id,
                DataContractId = e.DataContractId,
                OverallScore = e.OverallScore,
                PassedRules = e.PassedRules,
                FailedRules = e.FailedRules,
                TotalRules = e.TotalRules,
                AwaitingApprovalRules = e.AwaitingApprovalRules,
                HeldAlertRecipients = e.HeldAlertRecipients,
                ExecutionTimeMs = e.ExecutionTimeMs,
                CreatedTime = e.CreatedTime,
                RuleResults = e.RuleResults.Select(r => new DataQualityRuleResultData
                {
                    Id = r.Id,
                    DataContractRuleId = r.DataContractRuleId,
                    RuleName = r.DataContractRule.Name,
                    Passed = r.Passed,
                    Score = r.Score,
                    ActualValue = r.ActualValue,
                    ExpectedValue = r.ExpectedValue,
                    Message = r.Message,
                    ExecutionTimeMs = r.ExecutionTimeMs
                }).ToList()
            })
            .ToListAsync(cancellationToken);

        return evaluations;
    }

    private async Task<DataQualityRuleResult> EvaluateRuleAsync(
        DataContractRule rule,
        DataContract contract,
        DataQualityRuleCatalog catalog,
        IDataSourceProvider provider,
        DatabaseEngineType engineType,
        string? configuredDatabase,
        RowSecurityContext rowSecurity,
        McpSettingsData pii,
        CancellationToken cancellationToken)
    {
        var ruleStopwatch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // A caller's cancellation propagates (nothing is stored); every other failure becomes a failed rule result.
        try
        {
            if (!TryPrepareQuery(rule, contract, catalog, engineType, configuredDatabase, rowSecurity, out var query, out var rejection))
            {
                ruleStopwatch.Stop();

                return Failed(rule, rejection, ruleStopwatch.Elapsed.TotalMilliseconds);
            }

            timeoutCts.CancelAfter(RuleTimeout);

            // §1.5 — the read-only path: a READ ONLY transaction on PostgreSQL; on a host-managed source the host
            // policy, host column masking and the generic host error. The provider reports a cancellation as a failed
            // result, so the caller's token is checked here.
            var result = await provider.ExecuteReadOnlyQueryAsync(contract.DataSource, query.Sql, query.Parameters, timeoutCts.Token);
            ruleStopwatch.Stop();
            cancellationToken.ThrowIfCancellationRequested();

            if (!result.Success)
            {
                var timedOut = timeoutCts.IsCancellationRequested;
                logger.LogWarning(
                    "Data-quality rule {RuleId} ({RuleType}) of contract {ContractId} on data source {DataSourceId} failed on the data source after {ElapsedMs} ms (timed out: {TimedOut})",
                    rule.Id,
                    rule.RuleType,
                    contract.Id,
                    contract.DataSourceId,
                    ruleStopwatch.ElapsedMilliseconds,
                    timedOut);

                return Failed(rule, timedOut ? TimedOutMessage : FailureMessage(contract.DataSource, exception: null), ruleStopwatch.Elapsed.TotalMilliseconds);
            }

            try
            {
                var row = result.Rows.FirstOrDefault();
                if (row != null && rule.RuleType == DataContractRuleType.CustomSql)
                {
                    row = MaskCustomSqlText(row, query.Sql, pii, engineType);
                }

                return InterpretResult(rule, row, ruleStopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidCastException or FormatException or OverflowException)
            {
                // A conversion error quotes the value it could not convert, so neither the result nor the log carries it.
                LogFailure(rule, contract, ruleStopwatch, ex);

                return Failed(rule, UnexpectedResultMessage, ruleStopwatch.Elapsed.TotalMilliseconds);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            ruleStopwatch.Stop();
            LogFailure(rule, contract, ruleStopwatch, ex);
            var message = timeoutCts.IsCancellationRequested ? TimedOutMessage : FailureMessage(contract.DataSource, ex);

            return Failed(rule, message, ruleStopwatch.Elapsed.TotalMilliseconds);
        }
    }

    // Builds the rule's query and passes it through the read-only gate. A rejection is derived from the rule's own
    // configuration or SQL, never from data, so it is returned as is; the log carries identifiers only (§1.11).
    private bool TryPrepareQuery(
        DataContractRule rule,
        DataContract contract,
        DataQualityRuleCatalog catalog,
        DatabaseEngineType engineType,
        string? configuredDatabase,
        RowSecurityContext rowSecurity,
        [NotNullWhen(true)] out DataQualityQuery? query,
        [NotNullWhen(false)] out string? rejection)
    {
        query = null;

        // A CustomSql rule could project the host's masked columns, so it never runs on a host-managed source.
        if (rule.RuleType == DataContractRuleType.CustomSql && contract.DataSource.HostManagedKey != null)
        {
            rejection = DataQualityRuleGuard.HostManagedCustomSqlMessage;
            LogRejection(rule, contract, "host-managed source");
            return false;
        }

        // The SQL is generated only for the data source's own tables and columns (the CustomSql rule's own SQL excepted),
        // spelled as its metadata spells them, also for a rule stored before names were resolved.
        var confined = catalog.Confine(contract.SchemaName, contract.TableName, rule.RuleType, rule.ColumnName, rule.Configuration);
        if (confined.Problem != null)
        {
            rejection = confined.Problem;
            LogRejection(rule, contract, "names an object outside the data source's metadata");
            return false;
        }

        DataQualityQuery generated;
        try
        {
            // Enrich config with schema/table from the contract
            var enrichedConfig = EnrichConfig(confined.Configuration ?? rule.Configuration, confined.SchemaName, confined.TableName);
            var enrichedRule = new DataContractRule
            {
                Id = rule.Id,
                Name = rule.Name,
                RuleType = rule.RuleType,
                ColumnName = confined.ColumnName,
                Configuration = enrichedConfig,
                Severity = rule.Severity,
                Weight = rule.Weight,
                IsEnabled = rule.IsEnabled,
                DataContractId = rule.DataContractId
            };

            generated = sqlGenerator.GenerateSql(enrichedRule, engineType);
        }
        catch (NotSupportedException)
        {
            rejection = $"Rule type {rule.RuleType} is not supported on {engineType}.";
            LogRejection(rule, contract, "unsupported engine");
            return false;
        }
        catch (Exception ex)
        {
            // The generator reads only the rule's configuration, so its message cannot carry data.
            rejection = $"Invalid rule configuration: {ex.Message}";
            LogRejection(rule, contract, "invalid configuration");
            return false;
        }

        // Re-checked on every run, so a rule saved before the gate existed (or edited in the database) cannot write.
        var report = gate.Evaluate(DataQualityRuleGuard.GateRequest(generated.Sql, engineType, contract.DataSource.HostManagedKey, configuredDatabase, rowSecurity));
        var gateRejection = DataQualityRuleGuard.RejectionOf(report);
        if (gateRejection != null)
        {
            rejection = gateRejection;
            LogRejection(rule, contract, "read-only gate");
            return false;
        }

        query = generated with { Sql = report.FinalSql };
        rejection = null;
        return true;
    }

    // A CustomSql rule's own text columns (actual_value, message) become the rule result's value and message, which its
    // history and alerts carry, so a PII column read into them — by its own name, by an alias over it or by a custom
    // pattern — is masked before the result is built (§1.6/§1.11). While PII detection is on, a text value that looks like
    // PII is masked too, whatever its column is called (MAX(iban) AS actual_value), and a screen that cannot finish counts
    // as a hit. The numeric columns (passed, score), and a value the database returns as a number, are not text.
    private Dictionary<string, object?> MaskCustomSqlText(Dictionary<string, object?> row, string sql, McpSettingsData pii, DatabaseEngineType engineType)
    {
        var customPatterns = pii.CustomPiiPatterns.Count > 0 ? pii.CustomPiiPatterns : null;
        var piiColumns = PiiRowMasker.ResolvePiiColumns(
            guardrail,
            row.Keys,
            [],
            pii.EnablePiiDetection,
            customPatterns,
            sql,
            engineType.ToString());
        var textColumns = row.Keys
            .Where(x => CustomSqlTextColumns.Contains(x))
            .Where(x => piiColumns.Contains(x) || LooksLikePii(row[x]))
            .ToList();

        return textColumns.Count == 0 ? row : guardrail.MaskPiiValues(row, textColumns);

        bool LooksLikePii(object? value)
        {
            return pii.EnablePiiDetection
                && value is string { Length: > 0 } text
                && PiiValueScreen.ContainsPiiValue([text], customPatterns);
        }
    }

    private void LogRejection(DataContractRule rule, DataContract contract, string reason)
    {
        logger.LogWarning(
            "Data-quality rule {RuleId} ({RuleType}) of contract {ContractId} on data source {DataSourceId} was not run: {Reason}",
            rule.Id,
            rule.RuleType,
            contract.Id,
            contract.DataSourceId,
            reason);
    }

    // The exception type only (§1.11): its message can quote row values.
    private void LogFailure(DataContractRule rule, DataContract contract, Stopwatch ruleStopwatch, Exception ex)
    {
        logger.LogWarning(
            "Data-quality rule {RuleId} ({RuleType}) of contract {ContractId} on data source {DataSourceId} failed after {ElapsedMs} ms with {ExceptionType}",
            rule.Id,
            rule.RuleType,
            contract.Id,
            contract.DataSourceId,
            ruleStopwatch.ElapsedMilliseconds,
            ex.GetType().Name);
    }

    // A disabled source says so, with the refusal's generic message (a provider that catches the refusal reports a failed
    // result, so the source's own state is read too); any other failure keeps its generic message.
    private static string FailureMessage(DataSource dataSource, Exception? exception)
    {
        if (exception is DataSourceDisabledException || dataSource.DisabledTime != null)
        {
            return DataSourceDisabledException.RefusalMessage;
        }

        return dataSource.HostManagedKey == null ? ExecutionFailedMessage : DatabaseProvider.HostQueryFailedMessage;
    }

    private static DataQualityRuleResult Failed(DataContractRule rule, string message, double executionTimeMs)
    {
        return new DataQualityRuleResult
        {
            DataContractRuleId = rule.Id,
            Passed = false,
            Score = 0,
            ActualValue = "Error",
            Message = message,
            ExecutionTimeMs = executionTimeMs
        };
    }

    private DataQualityRuleResult InterpretResult(DataContractRule rule, Dictionary<string, object?>? dict, double executionTimeMs)
    {
        if (dict == null)
        {
            return new DataQualityRuleResult
            {
                DataContractRuleId = rule.Id,
                Passed = false,
                Score = 0,
                Message = "Query returned no results",
                ExecutionTimeMs = executionTimeMs
            };
        }

        using var config = JsonDocument.Parse(rule.Configuration);

        switch (rule.RuleType)
        {
            case DataContractRuleType.Freshness:
                {
                    var failed = Convert.ToInt32(dict["failed"]);
                    var actualValue = dict.ContainsKey("actual_value") ? Convert.ToString(dict["actual_value"]) : null;
                    return new DataQualityRuleResult
                    {
                        DataContractRuleId = rule.Id,
                        Passed = failed == 0,
                        Score = failed == 0 ? 100 : 0,
                        ActualValue = actualValue != null ? $"{actualValue} minutes" : null,
                        ExpectedValue = config.RootElement.TryGetProperty("maxAgeMinutes", out var maxAge) ? $"< {maxAge} minutes" : null,
                        Message = failed == 0 ? "Data is fresh" : "Data is stale",
                        ExecutionTimeMs = executionTimeMs
                    };
                }

            case DataContractRuleType.Volume:
                {
                    var rowCount = Convert.ToInt64(dict["row_count"]);
                    var minRows = config.RootElement.TryGetProperty("minRows", out var minEl) ? (long?)minEl.GetInt64() : null;
                    var maxRows = config.RootElement.TryGetProperty("maxRows", out var maxEl) ? (long?)maxEl.GetInt64() : null;

                    var passed = true;
                    if (minRows.HasValue && rowCount < minRows.Value) passed = false;
                    if (maxRows.HasValue && rowCount > maxRows.Value) passed = false;

                    var expected = (minRows, maxRows) switch
                    {
                        (not null, not null) => $"{minRows} - {maxRows}",
                        (not null, null) => $">= {minRows}",
                        (null, not null) => $"<= {maxRows}",
                        _ => null
                    };

                    return new DataQualityRuleResult
                    {
                        DataContractRuleId = rule.Id,
                        Passed = passed,
                        Score = passed ? 100 : 0,
                        ActualValue = rowCount.ToString(),
                        ExpectedValue = expected,
                        Message = passed ? "Row count within expected range" : "Row count outside expected range",
                        ExecutionTimeMs = executionTimeMs
                    };
                }

            case DataContractRuleType.NullRate:
                {
                    var nullPercent = dict["null_percent"] != null ? Convert.ToDouble(dict["null_percent"]) : 0;
                    var maxNullPercent = config.RootElement.TryGetProperty("maxNullPercent", out var maxNullEl) ? maxNullEl.GetDouble() : 0;
                    var passed = nullPercent <= maxNullPercent;

                    return new DataQualityRuleResult
                    {
                        DataContractRuleId = rule.Id,
                        Passed = passed,
                        Score = passed ? 100 : Math.Max(0, 100 - (nullPercent - maxNullPercent)),
                        ActualValue = $"{nullPercent:F2}%",
                        ExpectedValue = $"<= {maxNullPercent}%",
                        Message = passed ? "Null rate within threshold" : $"Null rate {nullPercent:F2}% exceeds threshold {maxNullPercent}%",
                        ExecutionTimeMs = executionTimeMs
                    };
                }

            case DataContractRuleType.Uniqueness:
                {
                    var duplicateCount = Convert.ToInt64(dict["duplicate_count"]);
                    var passed = duplicateCount == 0;

                    return new DataQualityRuleResult
                    {
                        DataContractRuleId = rule.Id,
                        Passed = passed,
                        Score = passed ? 100 : 0,
                        ActualValue = duplicateCount.ToString(),
                        ExpectedValue = "0",
                        Message = passed ? "All values are unique" : $"{duplicateCount} duplicate values found",
                        ExecutionTimeMs = executionTimeMs
                    };
                }

            case DataContractRuleType.Referential:
                {
                    var orphaned = Convert.ToInt64(dict["orphaned"]);
                    var passed = orphaned == 0;

                    return new DataQualityRuleResult
                    {
                        DataContractRuleId = rule.Id,
                        Passed = passed,
                        Score = passed ? 100 : 0,
                        ActualValue = orphaned.ToString(),
                        ExpectedValue = "0",
                        Message = passed ? "All references are valid" : $"{orphaned} orphaned records found",
                        ExecutionTimeMs = executionTimeMs
                    };
                }

            case DataContractRuleType.Range:
                {
                    var outOfRange = Convert.ToInt64(dict["out_of_range"]);
                    var total = dict.ContainsKey("total") ? Convert.ToInt64(dict["total"]) : 0;
                    var passed = outOfRange == 0;

                    return new DataQualityRuleResult
                    {
                        DataContractRuleId = rule.Id,
                        Passed = passed,
                        Score = total > 0 ? Math.Round((1 - (double)outOfRange / total) * 100, 2) : 100,
                        ActualValue = $"{outOfRange} out of range",
                        ExpectedValue = "0 out of range",
                        Message = passed ? "All values within range" : $"{outOfRange} values out of range",
                        ExecutionTimeMs = executionTimeMs
                    };
                }

            case DataContractRuleType.Pattern:
                {
                    var nonMatching = Convert.ToInt64(dict["non_matching"]);
                    var passed = nonMatching == 0;

                    return new DataQualityRuleResult
                    {
                        DataContractRuleId = rule.Id,
                        Passed = passed,
                        Score = passed ? 100 : 0,
                        ActualValue = nonMatching.ToString(),
                        ExpectedValue = "0",
                        Message = passed ? "All values match pattern" : $"{nonMatching} values don't match pattern",
                        ExecutionTimeMs = executionTimeMs
                    };
                }

            case DataContractRuleType.CustomSql:
                {
                    // Custom SQL should return: passed (0/1), score (optional), actual_value (optional), message (optional)
                    var passed = dict.ContainsKey("passed") ? Convert.ToInt32(dict["passed"]) == 1 : false;
                    var score = dict.ContainsKey("score") ? Convert.ToDouble(dict["score"]) : (passed ? 100.0 : 0.0);
                    var actualValue = dict.ContainsKey("actual_value") ? Convert.ToString(dict["actual_value"]) : null;
                    var message = dict.ContainsKey("message") ? Convert.ToString(dict["message"]) : null;

                    return new DataQualityRuleResult
                    {
                        DataContractRuleId = rule.Id,
                        Passed = passed,
                        Score = score,
                        ActualValue = actualValue,
                        Message = message ?? (passed ? "Custom check passed" : "Custom check failed"),
                        ExecutionTimeMs = executionTimeMs
                    };
                }

            default:
                return new DataQualityRuleResult
                {
                    DataContractRuleId = rule.Id,
                    Passed = false,
                    Score = 0,
                    Message = $"Unsupported rule type: {rule.RuleType}",
                    ExecutionTimeMs = executionTimeMs
                };
        }
    }

    private static double ComputeOverallScore(List<DataContractRule> rules, List<DataQualityRuleResult> results)
    {
        if (results.Count == 0) return 100;

        double weightedSum = 0;
        double weightTotal = 0;

        foreach (var result in results)
        {
            var rule = rules.First(r => r.Id == result.DataContractRuleId);
            var severityMultiplier = GetSeverityMultiplier(rule.Severity);
            var weight = rule.Weight * severityMultiplier;

            weightedSum += result.Score * weight;
            weightTotal += weight;
        }

        return weightTotal > 0 ? Math.Round(weightedSum / weightTotal, 2) : 0;
    }

    private static double GetSeverityMultiplier(DataContractSeverity severity) => severity switch
    {
        DataContractSeverity.Critical => 4,
        DataContractSeverity.High => 3,
        DataContractSeverity.Medium => 2,
        DataContractSeverity.Low => 1,
        _ => 1
    };

    private static async Task UpsertScoreAsync(BeaconContext context, DataContract contract, double score, CancellationToken cancellationToken)
    {
        var existing = await context.DataQualityScores
            .Where(s => s.DataSourceId == contract.DataSourceId)
            .Where(s => s.SchemaName == contract.SchemaName)
            .Where(s => s.TableName == contract.TableName)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != null)
        {
            existing.PreviousScore = existing.Score;
            existing.Score = score;
            existing.EvaluatedAt = DateTime.UtcNow;
            existing.TrendDirection = DetermineTrend(existing.PreviousScore, score);
        }
        else
        {
            context.DataQualityScores.Add(new DataQualityScore
            {
                DataSourceId = contract.DataSourceId,
                SchemaName = contract.SchemaName,
                TableName = contract.TableName,
                Score = score,
                EvaluatedAt = DateTime.UtcNow,
                TrendDirection = DataQualityTrendDirection.Stable
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static DataQualityTrendDirection DetermineTrend(double? previousScore, double currentScore)
    {
        if (!previousScore.HasValue) return DataQualityTrendDirection.Stable;

        var diff = currentScore - previousScore.Value;
        if (diff > 1) return DataQualityTrendDirection.Improving;
        if (diff < -1) return DataQualityTrendDirection.Degrading;
        return DataQualityTrendDirection.Stable;
    }

    private static string EnrichConfig(string configJson, string schema, string table)
    {
        using var doc = JsonDocument.Parse(configJson);
        var dict = new Dictionary<string, JsonElement>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = prop.Value;
        }

        // Add schema and table if not already present
        using var ms = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();

        writer.WriteString("schema", schema);
        writer.WriteString("table", table);

        foreach (var kvp in dict)
        {
            if (kvp.Key is "schema" or "table") continue;
            writer.WritePropertyName(kvp.Key);
            kvp.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.Flush();

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static DataQualityEvaluationData MapEvaluation(DataQualityEvaluation evaluation, List<DataContractRule> rules)
    {
        return new DataQualityEvaluationData
        {
            Id = evaluation.Id,
            DataContractId = evaluation.DataContractId,
            OverallScore = evaluation.OverallScore,
            PassedRules = evaluation.PassedRules,
            FailedRules = evaluation.FailedRules,
            TotalRules = evaluation.TotalRules,
            AwaitingApprovalRules = evaluation.AwaitingApprovalRules,
            HeldAlertRecipients = evaluation.HeldAlertRecipients,
            ExecutionTimeMs = evaluation.ExecutionTimeMs,
            CreatedTime = evaluation.CreatedTime,
            RuleResults = evaluation.RuleResults.Select(r =>
            {
                var rule = rules.FirstOrDefault(ru => ru.Id == r.DataContractRuleId);
                return new DataQualityRuleResultData
                {
                    Id = r.Id,
                    DataContractRuleId = r.DataContractRuleId,
                    RuleName = rule?.Name ?? "Unknown",
                    Passed = r.Passed,
                    Score = r.Score,
                    ActualValue = r.ActualValue,
                    ExpectedValue = r.ExpectedValue,
                    Message = r.Message,
                    ExecutionTimeMs = r.ExecutionTimeMs
                };
            }).ToList()
        };
    }
}
