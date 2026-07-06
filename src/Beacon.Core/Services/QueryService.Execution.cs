using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Adapters;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Helpers.File;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Models.QueryExecutionHistory;
using Beacon.Core.Models.Recipients;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Validators;

namespace Beacon.Core.Services;

internal partial class QueryService
{

    public async Task<QueryResult> ExecuteQuery(int subscriptionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var subscription = await context.Subscriptions
            .Include(x => x.Recipients)
            .Include(x => x.Query)
                .ThenInclude(q => q.Steps)
                    .ThenInclude(s => s.DataSource)
            .Include(x => x.Query)
                .ThenInclude(q => q.Steps)
                    .ThenInclude(s => s.Parameters)
            .Where(x => x.Id == subscriptionId)
            .SingleAsync(cancellationToken);

        // Use the advanced step-based execution with stored final query if available
        var executionResult = await ExecuteQueryAdvanced(
            subscription.QueryId,
            finalQuery: subscription.Query.FinalQuery,
            parameters: null,
            cancellationToken);

        // Convert QueryExecutionResult to QueryResult for backward compatibility
        QueryResult queryResult;

        if (executionResult.FinalResult != null)
        {
            // Multi-step query or query with final result
            queryResult = executionResult.FinalResult;
        }
        else if (executionResult.StepResults.Any() && executionResult.StepResults.Last().Success)
        {
            // Single-step query - convert the step result to QueryResult
            var lastStep = executionResult.StepResults.Last();
            queryResult = new QueryResult
            {
                QueryResults = JsonSerializer.Serialize(lastStep.PreviewResults),
                TotalRecords = lastStep.TotalRows,
                DataSourceName = lastStep.DataSourceName,
                SqlQuery = lastStep.SqlQuery,
                SubscriptionName = subscription.Query.Name,
                SubscriptionId = subscriptionId,
                TopRecords = lastStep.PreviewResults,
                AllRecords = lastStep.AllResults,
                ExecutionTimeMs = lastStep.ExecutionTimeMs,
                TimedOut = !lastStep.Success,
                Recipients = new List<RecipientData>()
            };
        }
        else
        {
            // Execution failed
            throw new BeaconException($"Query execution failed: {executionResult.ErrorMessage ?? "Unknown error"}");
        }

        // Apply subscription-specific settings and recipients
        queryResult.Recipients = subscription.Recipients.Select(r => new RecipientData
        {
            RecipientId = r.Id,
            Name = r.Name,
            Description = r.Description,
            Destination = r.Destination,
            NotificationType = r.NotificationType,
            HeadersJson = r.HeadersJson
        }).ToList();

        queryResult.MaxRows = subscription.MaxRows ?? Constants.Query.DefaultMaxRows;

        queryResult.TopRecords = queryResult.TopRecords.Take(queryResult.MaxRows.Value).ToList();
        queryResult.AllRecords = queryResult.AllRecords.Take(queryResult.MaxRows.Value).ToList();

        // Need to create a new QueryResult with updated QueryResults since it's init-only
        queryResult = new QueryResult
        {
            QueryResults = JsonSerializer.Serialize(queryResult.TopRecords),
            TotalRecords = queryResult.TotalRecords,
            DataSourceName = queryResult.DataSourceName,
            SqlQuery = queryResult.SqlQuery,
            SubscriptionName = subscription.Query.Name,
            SubscriptionId = subscriptionId,
            ShowQuery = queryResult.ShowQuery,
            MaxRows = queryResult.MaxRows,
            Recipients = queryResult.Recipients,
            TopRecords = queryResult.TopRecords,
            AllRecords = queryResult.AllRecords,
            ExecutionTimeMs = queryResult.ExecutionTimeMs,
            TimedOut = queryResult.TimedOut,
            SaveResults = subscription.StoreResults
        };


        return queryResult;
    }

    public async Task<BaseResponse> UpdateQuery(QueryData queryData, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = await context.Queries
            .Include(x => x.Steps)
                .ThenInclude(s => s.Parameters)
            .Where(x => x.Id == queryData.QueryId)
            .SingleAsync(cancellationToken);

        // Determine if SQL has changed (for versioning)
        var sqlChanged = HasSqlChanged(query, queryData);

        // Snapshot current state before modifying (archive the old version)
        if (sqlChanged)
        {
            // When approval workflow is enabled, create a PendingApproval version instead of applying changes directly
            if (beaconConfiguration.ApprovalWorkflow.Enabled)
            {
                var pendingVersion = await queryVersionService.CreateVersionAsync(
                    query.Id, null, "UserEdit", "Submitted for approval",
                    Data.Enums.QueryVersionStatus.PendingApproval, cancellationToken);

                // Create the approval request
                await using var approvalContext = await contextFactory.CreateDbContextAsync(cancellationToken);
                approvalContext.QueryApprovalRequests.Add(new Data.Entities.QueryApprovalRequest
                {
                    QueryId = query.Id,
                    QueryVersionId = pendingVersion.Id,
                    Status = Data.Enums.ApprovalStatus.Pending,
                    ChangeSummary = "SQL query modified"
                });
                await approvalContext.SaveChangesAsync(cancellationToken);

                return new BaseResponse
                {
                    Message = "Changes submitted for approval",
                    Success = true
                };
            }

            await queryVersionService.CreateVersionAsync(
                query.Id, null, "UserEdit", null,
                Data.Enums.QueryVersionStatus.Archived, cancellationToken);
        }

        // Update basic query properties
        query.Name = queryData.Name;
        query.Description = queryData.Description;
        query.FinalQuery = queryData.FinalQuery;

        // Handle backward compatibility - if no steps provided, update from legacy properties
        if (!queryData.Steps.Any() && !string.IsNullOrEmpty(queryData.SqlValue))
        {
            QueryValidator.CheckForFlaggedWords(queryData.SqlValue);
            QueryValidator.CheckForParameters(queryData.SqlValue, queryData.Parameters);

            // Update the first (and typically only) step for backward compatibility
            var firstStep = query.Steps.OrderBy(s => s.StepOrder).First();
            firstStep.SqlValue = queryData.SqlValue;
            firstStep.DataSourceId = queryData.DataSourceId;

            // Remove existing parameters
            foreach (var param in firstStep.Parameters.ToList())
            {
                context.QueryStepParameters.Remove(param);
            }

            // Add new parameters
            var newParams = ParameterEntityFactory.CreateQueryStepParameters(queryData.Parameters, firstStep.Id);
            context.QueryStepParameters.AddRange(newParams);
        }
        else
        {
            // Handle multi-step updates — sync the full step set: update matched steps, create new
            // ones (StepId == 0 or unmatched), and delete steps the caller removed. Matching only by
            // StepId silently dropped added steps, ignored deletions, and never persisted StepOrder.
            var incomingStepIds = queryData.Steps
                .Where(x => x.StepId != 0)
                .Select(x => x.StepId)
                .ToHashSet();

            var removedSteps = query.Steps
                .Where(x => !incomingStepIds.Contains(x.Id))
                .ToList();

            foreach (var removed in removedSteps)
            {
                foreach (var param in removed.Parameters.ToList())
                {
                    context.QueryStepParameters.Remove(param);
                }
                context.QuerySteps.Remove(removed);
            }

            foreach (var stepData in queryData.Steps)
            {
                QueryValidator.CheckForFlaggedWords(stepData.SqlValue);

                var step = stepData.StepId != 0
                    ? query.Steps.FirstOrDefault(x => x.Id == stepData.StepId)
                    : null;

                if (step == null)
                {
                    step = new QueryStep
                    {
                        QueryId = query.Id,
                        DataSourceId = stepData.DataSourceId,
                        StepOrder = stepData.StepOrder,
                        Name = stepData.Name,
                        Description = stepData.Description,
                        SqlValue = stepData.SqlValue,
                        Parameters = ParameterEntityFactory.CreateQueryStepParameters(stepData.Parameters, 0)
                    };
                    context.QuerySteps.Add(step);
                    continue;
                }

                step.DataSourceId = stepData.DataSourceId;
                step.StepOrder = stepData.StepOrder;
                step.Name = stepData.Name;
                step.Description = stepData.Description;
                step.SqlValue = stepData.SqlValue;

                foreach (var param in step.Parameters.ToList())
                {
                    context.QueryStepParameters.Remove(param);
                }

                var stepParams = ParameterEntityFactory.CreateQueryStepParameters(stepData.Parameters, step.Id);
                context.QueryStepParameters.AddRange(stepParams);
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        // Create new Active version after applying edits
        if (sqlChanged)
        {
            await queryVersionService.CreateVersionAsync(
                query.Id, null, "UserEdit", null,
                Data.Enums.QueryVersionStatus.Active, cancellationToken);
        }

        return new BaseResponse
        {
            Message = "Saved successfully",
            Success = true
        };
    }

    private static bool HasSqlChanged(Query query, QueryData queryData)
    {
        // Check FinalQuery change
        if (query.FinalQuery != queryData.FinalQuery) return true;

        // Check step SQL changes
        if (!queryData.Steps.Any() && !string.IsNullOrEmpty(queryData.SqlValue))
        {
            // Legacy single-step mode
            var firstStep = query.Steps.OrderBy(s => s.StepOrder).FirstOrDefault();
            return firstStep == null || firstStep.SqlValue != queryData.SqlValue || firstStep.DataSourceId != queryData.DataSourceId;
        }

        foreach (var stepData in queryData.Steps)
        {
            var existingStep = query.Steps.FirstOrDefault(s => s.Id == stepData.StepId);
            if (existingStep == null) return true;
            if (existingStep.SqlValue != stepData.SqlValue) return true;
            if (existingStep.DataSourceId != stepData.DataSourceId) return true;
        }

        return false;
    }

    private async Task<(List<IDictionary<string, object?>> Results, double ExecutionTimeMs, bool TimedOut)> ExecuteQueryAsync(
        DatabaseEngineType dbEngineType,
        string connectionString,
        string sqlQuery,
        Dictionary<string, object?>? parameters = null,
        int? timeoutSeconds = null)
    {
        await using var connection = DbConnectionFactory.CreateConnection(dbEngineType, connectionString);
        await connection.OpenAsync();

        // Replace newline, carriage return, and tab characters with a space
        var cleanedSql = sqlQuery.Replace("\n", " ")
            .Replace("\r", " ")
            .Replace("\t", " ")
            .Trim();

        var stopwatch = new System.Diagnostics.Stopwatch();
        stopwatch.Start();

        try
        {
            // Create a cancellation token source with timeout if specified
            using var timeoutCts = timeoutSeconds.HasValue
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value))
                : new CancellationTokenSource();

            // Combine with the provided cancellation token if needed
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);

            // Execute the query with parameterized values to prevent SQL injection.
            // Unbuffered so rows stream off the reader into a single list instead of
            // being materialized twice (Dapper buffer + ToList).
            var commandDefinition = new CommandDefinition(
                commandText: cleanedSql,
                parameters: parameters,  // Dapper handles parameterized queries securely
                commandTimeout: timeoutSeconds,
                flags: CommandFlags.None,
                cancellationToken: linkedCts.Token
            );

            var dapperRows = await connection.QueryAsync(commandDefinition);

            // Convert the dapper rows to a list of dictionaries for reflection and serialization
            var results = new List<IDictionary<string, object?>>();
            foreach (var dapperRow in dapperRows)
            {
                results.Add((IDictionary<string, object?>)dapperRow);
            }

            stopwatch.Stop();
            var executionTimeMs = stopwatch.Elapsed.TotalMilliseconds;
            return (results, executionTimeMs, false);
        }
        catch (OperationCanceledException)
        {
            // Query was cancelled due to timeout
            stopwatch.Stop();
            var executionTimeMs = stopwatch.Elapsed.TotalMilliseconds;

            // Return empty results with timeout indicator
            return (new List<IDictionary<string, object?>>(), executionTimeMs, true);
        }
        catch (Exception)
        {
            stopwatch.Stop();
            // Re-throw other exceptions
            throw;
        }
    }

    // ===== ENHANCED METHODS FOR CROSS-PROJECT FUNCTIONALITY =====

    public async Task<QueryExecutionResult> ExecuteQueryAdvanced(int queryId, string? finalQuery = null, List<ParameterValue>? parameters = null, CancellationToken cancellationToken = default)
    {
        var query = await GetQueryWithSteps(queryId, cancellationToken);

        // Use provided parameters or fall back to stored values from query
        var effectiveFinalQuery = finalQuery ?? query.FinalQuery;

        // All queries use the same execution path - handles single-DB, multi-step, and cross-DB!
        return await ExecuteQuerySteps(query, effectiveFinalQuery, parameters, cancellationToken);
    }

    public async Task<QueryStepResult> PreviewQueryStep(int queryId, int stepOrder, CancellationToken cancellationToken)
    {
        return await PreviewQueryStep(queryId, stepOrder, null, cancellationToken);
    }

    public async Task<QueryStepResult> PreviewQueryStep(int queryId, int stepOrder, List<ParameterValue>? parameters, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var step = await context.QuerySteps
            .Include(s => s.DataSource)
            .Include(s => s.Parameters)
            .Where(s => s.QueryId == queryId && s.StepOrder == stepOrder)
            .SingleAsync(cancellationToken);

        return await ExecuteStep(step, parameters);
    }
}
