using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities.DataQuality;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Models.DataQuality;

namespace Semantico.Core.Services;

public interface IDataQualityEvaluationService
{
    Task<DataQualityEvaluationData> EvaluateContractAsync(int dataContractId);
    Task<List<DataQualityScoreData>> GetLatestScoresAsync(int? dataSourceId);
    Task<List<DataQualityEvaluationData>> GetEvaluationHistoryAsync(int dataContractId, int take = 20);
}

internal class DataQualityEvaluationService(
    IDbContextFactory<SemanticoContext> contextFactory,
    IDataQualitySqlGenerator sqlGenerator,
    IEncryptionService encryptionService,
    ILogger<DataQualityEvaluationService> logger) : IDataQualityEvaluationService
{
    public async Task<DataQualityEvaluationData> EvaluateContractAsync(int dataContractId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var contract = await context.DataContracts
            .Include(c => c.Rules)
            .Include(c => c.DataSource)
            .FirstOrDefaultAsync(c => c.Id == dataContractId)
            ?? throw new Models.SemanticoException($"Data contract {dataContractId} not found");

        if (contract.DataSource.DatabaseEngineType == null)
            throw new Models.SemanticoException("Data contract's data source must be a database type");

        var connectionString = encryptionService.Decrypt(contract.DataSource.EncryptedConnectionData);
        var engineType = contract.DataSource.DatabaseEngineType.Value;
        var enabledRules = contract.Rules.Where(r => r.IsEnabled).ToList();

        var totalStopwatch = Stopwatch.StartNew();
        var ruleResults = new List<DataQualityRuleResult>();

        foreach (var rule in enabledRules)
        {
            var result = await EvaluateRuleAsync(rule, contract, engineType, connectionString);
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
            ExecutionTimeMs = totalStopwatch.Elapsed.TotalMilliseconds,
            RuleResults = ruleResults
        };

        context.DataQualityEvaluations.Add(evaluation);
        await context.SaveChangesAsync();

        // Upsert DataQualityScore
        await UpsertScoreAsync(context, contract, overallScore);

        return MapEvaluation(evaluation, enabledRules);
    }

    public async Task<List<DataQualityScoreData>> GetLatestScoresAsync(int? dataSourceId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var query = context.DataQualityScores.AsQueryable();

        if (dataSourceId.HasValue)
            query = query.Where(s => s.DataSourceId == dataSourceId.Value);

        return await query
            .OrderByDescending(s => s.EvaluatedAt)
            .Select(s => new DataQualityScoreData
            {
                Id = s.Id,
                DataSourceId = s.DataSourceId,
                SchemaName = s.SchemaName,
                TableName = s.TableName,
                Score = s.Score,
                EvaluatedAt = s.EvaluatedAt,
                TrendDirection = s.TrendDirection,
                PreviousScore = s.PreviousScore
            })
            .ToListAsync();
    }

    public async Task<List<DataQualityEvaluationData>> GetEvaluationHistoryAsync(int dataContractId, int take = 20)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var evaluations = await context.DataQualityEvaluations
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
            .ToListAsync();

        return evaluations;
    }

    private async Task<DataQualityRuleResult> EvaluateRuleAsync(
        DataContractRule rule,
        DataContract contract,
        DatabaseEngineType engineType,
        string connectionString)
    {
        var ruleStopwatch = Stopwatch.StartNew();

        try
        {
            // Enrich config with schema/table from the contract
            var enrichedConfig = EnrichConfig(rule.Configuration, contract.SchemaName, contract.TableName);
            var enrichedRule = new DataContractRule
            {
                Id = rule.Id,
                Name = rule.Name,
                RuleType = rule.RuleType,
                ColumnName = rule.ColumnName,
                Configuration = enrichedConfig,
                Severity = rule.Severity,
                Weight = rule.Weight,
                IsEnabled = rule.IsEnabled,
                DataContractId = rule.DataContractId
            };

            var sql = sqlGenerator.GenerateSql(enrichedRule, engineType);

            using var connection = DbConnectionFactory.CreateConnection(engineType, connectionString);
            await connection.OpenAsync();

            var row = await connection.QueryFirstOrDefaultAsync<dynamic>(sql, commandTimeout: 60);
            ruleStopwatch.Stop();

            return InterpretResult(rule, row, ruleStopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            ruleStopwatch.Stop();
            logger.LogWarning(ex, "Rule {RuleName} ({RuleId}) failed for contract {ContractId}",
                rule.Name, rule.Id, contract.Id);

            return new DataQualityRuleResult
            {
                DataContractRuleId = rule.Id,
                Passed = false,
                Score = 0,
                ActualValue = "Error",
                Message = $"Execution failed: {ex.Message}",
                ExecutionTimeMs = ruleStopwatch.Elapsed.TotalMilliseconds
            };
        }
    }

    private DataQualityRuleResult InterpretResult(DataContractRule rule, dynamic? row, double executionTimeMs)
    {
        if (row == null)
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

        var dict = (IDictionary<string, object>)row;
        var config = JsonDocument.Parse(rule.Configuration);

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

    private static async Task UpsertScoreAsync(SemanticoContext context, DataContract contract, double score)
    {
        var existing = await context.DataQualityScores
            .FirstOrDefaultAsync(s =>
                s.DataSourceId == contract.DataSourceId &&
                s.SchemaName == contract.SchemaName &&
                s.TableName == contract.TableName);

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

        await context.SaveChangesAsync();
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
        var doc = JsonDocument.Parse(configJson);
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
