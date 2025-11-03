using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;
using Semantico.Core.Adapters;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Models.Queries;
using Semantico.Core.Models.Subscriptions;

namespace Semantico.Core.Services.Shared;

/// <summary>
/// Shared orchestrator for multi-step query execution
/// Used by both QueryService and MigrationService
/// </summary>
internal class QueryExecutionOrchestrator
{
    private readonly ILogger<QueryExecutionOrchestrator> _logger;

    public QueryExecutionOrchestrator(ILogger<QueryExecutionOrchestrator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Executes a list of query steps across potentially different databases
    /// </summary>
    public async Task<List<StepExecutionResult>> ExecuteSteps(
        List<QueryStepData> steps,
        List<ParameterValue>? parameters,
        CancellationToken cancellationToken)
    {
        var results = new List<StepExecutionResult>();

        _logger.LogInformation("Executing {StepCount} query steps", steps.Count);

        foreach (var step in steps.OrderBy(s => s.StepOrder))
        {
            _logger.LogDebug("Executing step {StepOrder} against project {DataSourceName} ({DatabaseEngine})",
                step.StepOrder, step.DataSourceName, step.DatabaseEngineType);

            var result = await ExecuteSingleStep(step, parameters, cancellationToken);
            results.Add(result);

            if (!result.Success)
            {
                _logger.LogError("Step {StepOrder} failed: {ErrorMessage}", step.StepOrder, result.ErrorMessage);
                break; // Stop execution on first failure
            }
        }

        return results;
    }

    /// <summary>
    /// Executes a single query step
    /// </summary>
    private async Task<StepExecutionResult> ExecuteSingleStep(
        QueryStepData step,
        List<ParameterValue>? parameters,
        CancellationToken cancellationToken)
    {
        // Extract parameters for this step
        var stepParameters = ExtractStepParameters(step, parameters);
        var compiledSql = QueryHelper.CompileSql(step.SqlValue, stepParameters);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        List<Dictionary<string, object?>> results;
        bool timedOut = false;
        string? errorMessage = null;

        try
        {
            results = await ExecuteQueryAsync(step, compiledSql, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing step {StepOrder}", step.StepOrder);
            results = new List<Dictionary<string, object?>>();
            errorMessage = ex.Message;
        }

        stopwatch.Stop();

        return new StepExecutionResult
        {
            StepOrder = step.StepOrder,
            StepName = step.Name ?? $"Step {step.StepOrder}",
            SqlQuery = compiledSql,
            DataSourceName = step.DataSourceName,
            DatabaseEngineType = step.DatabaseEngineType,
            Results = results,
            TotalRows = results.Count,
            ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
            Success = errorMessage == null,
            ErrorMessage = errorMessage
        };
    }

    /// <summary>
    /// Executes a SQL query against the specified database
    /// </summary>
    private async Task<List<Dictionary<string, object?>>> ExecuteQueryAsync(
        QueryStepData step,
        string compiledSql,
        CancellationToken cancellationToken)
    {
        // Note: This method needs to be provided a connection string
        // In a real implementation, this would get the connection from project configuration
        // For now, this is a placeholder that should be replaced with actual connection logic

        throw new NotImplementedException(
            "ExecuteQueryAsync requires access to project connection strings. " +
            "This should be injected via a repository or configuration service.");
    }

    /// <summary>
    /// Extracts parameters relevant to a specific step
    /// </summary>
    private List<SubscriptionParamaterData> ExtractStepParameters(
        QueryStepData step,
        List<ParameterValue>? parameters)
    {
        if (parameters == null || !parameters.Any())
            return new List<SubscriptionParamaterData>();

        return step.Parameters.Select(p =>
        {
            var value = parameters.FirstOrDefault(param => param.Name == p.Name)?.Value ?? "";
            return new SubscriptionParamaterData
            {
                QueryPlaceholder = p.Name,
                Value = value
            };
        }).ToList();
    }
}

/// <summary>
/// Result of executing a single query step
/// </summary>
public class StepExecutionResult
{
    public int StepOrder { get; set; }
    public string StepName { get; set; } = null!;
    public string SqlQuery { get; set; } = null!;
    public string DataSourceName { get; set; } = null!;
    public DatabaseEngineType DatabaseEngineType { get; set; }
    public List<Dictionary<string, object?>> Results { get; set; } = new();
    public int TotalRows { get; set; }
    public double ExecutionTimeMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
