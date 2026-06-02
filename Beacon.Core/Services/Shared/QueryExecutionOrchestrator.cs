using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Services.Providers;

namespace Beacon.Core.Services.Shared;

/// <summary>
/// Shared orchestrator for multi-step query execution
/// Used by both QueryService and MigrationService
/// </summary>
internal class QueryExecutionOrchestrator(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    ILogger<QueryExecutionOrchestrator> logger)
{
    private readonly ILogger<QueryExecutionOrchestrator> _logger = logger;

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
            var dataSourceInfo = step.DatabaseEngineType.HasValue
                ? $"{step.DataSourceName} ({step.DatabaseEngineType})"
                : $"{step.DataSourceName} ({step.DataSourceType})";

            _logger.LogDebug("Executing step {StepOrder} against data source {DataSourceInfo}",
                step.StepOrder, dataSourceInfo);

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
        var (parameterizedSql, sqlParameters) = QueryHelper.PrepareParameterizedQuery(step.SqlValue, stepParameters);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        List<Dictionary<string, object?>> results;
        bool timedOut = false;
        string? errorMessage = null;

        try
        {
            results = await ExecuteQueryAsync(step, parameterizedSql, sqlParameters, cancellationToken);
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
            SqlQuery = parameterizedSql,
            DataSourceName = step.DataSourceName,
            DataSourceType = step.DataSourceType,
            DatabaseEngineType = step.DatabaseEngineType,
            Results = results,
            TotalRows = results.Count,
            ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
            Success = errorMessage == null,
            ErrorMessage = errorMessage
        };
    }

    /// <summary>
    /// Executes a query against the specified data source using the appropriate provider
    /// </summary>
    private async Task<List<Dictionary<string, object?>>> ExecuteQueryAsync(
        QueryStepData step,
        string parameterizedSql,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = await context.DataSources
            .Where(ds => ds.Id == step.DataSourceId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconException($"Data source {step.DataSourceId} not found");

        // Get appropriate provider based on data source type
        var provider = providerFactory.GetProvider(dataSource.DataSourceType);

        // Execute using provider
        var result = await provider.ExecuteQueryAsync(
            dataSource,
            parameterizedSql,
            parameters,
            cancellationToken);

        if (!result.Success)
        {
            throw new BeaconException(result.ErrorMessage ?? "Query execution failed");
        }

        return result.Rows;
    }

    /// <summary>
    /// Extracts parameters relevant to a specific step
    /// </summary>
    private List<SubscriptionParameterData> ExtractStepParameters(
        QueryStepData step,
        List<ParameterValue>? parameters)
    {
        if (parameters == null || !parameters.Any())
            return new List<SubscriptionParameterData>();

        return step.Parameters.Select(p =>
        {
            var value = parameters.FirstOrDefault(param => param.Name == p.Name)?.Value ?? "";
            return new SubscriptionParameterData
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
    public DataSourceType DataSourceType { get; set; }
    public DatabaseEngineType? DatabaseEngineType { get; set; }
    public List<Dictionary<string, object?>> Results { get; set; } = new();
    public int TotalRows { get; set; }
    public double ExecutionTimeMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
