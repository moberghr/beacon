using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Adapters;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
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

        // Re-checked on the steps loaded here, which are the ones that run, before any of them does: the run's actor (a
        // scheduled run's owner, or the caller of a request) may still run stored SQL on every data source they read.
        var dataSourceIds = subscription.Query.Steps
            .Select(x => x.DataSourceId)
            .Distinct()
            .Order()
            .ToList();
        if (!await executionAccess.AllowsAsync(ProjectAction.Run, dataSourceIds, "subscription", subscriptionId, cancellationToken))
        {
            throw new ExecutionRefusedException(dataSourceIds, "Running this subscription's query is not allowed.");
        }

        // Rows reach recipients, notifications, stored results and tasks, so they are PII-masked as the strictest live
        // project linking any of the data sources asks (§1.6).
        var pii = await PiiSettingsAsync(dataSourceIds, cancellationToken);

        // The step-based execution with the stored final query, on the steps checked above
        // The subscription's own timeout bounds each statement when it is shorter than Beacon's.
        var statementTimeout = subscription.TimeoutSeconds is > 0 ? TimeSpan.FromSeconds(subscription.TimeoutSeconds.Value) : (TimeSpan?)null;

        var executionResult = await ExecuteQuerySteps(
            subscription.Query,
            subscription.Query.FinalQuery,
            null,
            pii,
            statementTimeout,
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

        // The fetch itself is capped at ExecutionRowCeiling (a larger result failed the run above), so this trims an
        // already bounded, exactly counted result to what the subscription delivers.
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
            SaveResults = subscription.StoreResults,
            DataSourceIds = executionResult.DataSourceIds
        };


        return queryResult;
    }

    public async Task<BaseResponse> UpdateQuery(QueryData queryData, CancellationToken cancellationToken)
    {
        // Before anything is written — including the version snapshot or approval request below.
        ValidateFinalQuery(queryData.FinalQuery);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = await context.Queries
            .Include(x => x.Steps)
                .ThenInclude(s => s.Parameters)
            .Where(x => x.Id == queryData.QueryId)
            .SingleAsync(cancellationToken);

        // Determine if SQL has changed (for versioning)
        var sqlChanged = HasSqlChanged(query, queryData);

        if (sqlChanged)
        {
            // Check the edit before anything is written — a direct save and a submission alike.
            foreach (var stepData in queryData.Steps)
            {
                QueryValidator.CheckForFlaggedWords(stepData.SqlValue, readOnlyAstValidator.MaxSqlChars);
            }

            await EnsureStepsPassTheGateAsync(LegacyStepOrSteps(queryData), cancellationToken);

            // History must already hold the SQL this edit replaces; the active version normally does.
            await queryVersionService.EnsureBaselineVersionAsync(query.Id, cancellationToken);

            // When approval workflow is enabled, create a PendingApproval version instead of applying changes directly.
            // Approving applies the version's steps to the live query, so the version must hold the submitted edit —
            // snapshotting the stored query would approve the old SQL back in.
            if (beaconConfiguration.ApprovalWorkflow.Enabled)
            {
                var pendingVersion = await queryVersionService.CreateProposedVersionAsync(
                    query.Id, queryData, userContext.UserId, "UserEdit", "Submitted for approval", cancellationToken);

                // Create the approval request
                await using var approvalContext = await contextFactory.CreateDbContextAsync(cancellationToken);
                approvalContext.QueryApprovalRequests.Add(new Data.Entities.QueryApprovalRequest
                {
                    QueryId = query.Id,
                    QueryVersionId = pendingVersion.Id,
                    Status = Data.Enums.ApprovalStatus.Pending,
                    RequestedByUserId = userContext.UserId,
                    RequestedByUserName = userContext.DisplayName ?? userContext.UserName,
                    ChangeSummary = "SQL query modified"
                });
                await approvalContext.SaveChangesAsync(cancellationToken);

                return new BaseResponse
                {
                    Message = "Changes submitted for approval",
                    Success = true
                };
            }
        }

        // Update basic query properties
        query.Name = queryData.Name;
        query.Description = queryData.Description;
        query.FinalQuery = queryData.FinalQuery;

        // Handle backward compatibility - if no steps provided, update from legacy properties
        if (!queryData.Steps.Any() && !string.IsNullOrEmpty(queryData.SqlValue))
        {
            QueryValidator.CheckForFlaggedWords(queryData.SqlValue, readOnlyAstValidator.MaxSqlChars);
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
                QueryValidator.CheckForFlaggedWords(stepData.SqlValue, readOnlyAstValidator.MaxSqlChars);

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
                query.Id, userContext.UserId, "UserEdit", null,
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

    // The legacy single-step form (no steps, the SQL on the query itself) as the step it updates.
    private static IReadOnlyList<QueryStepData> LegacyStepOrSteps(QueryData queryData)
    {
        if (queryData.Steps.Any() || string.IsNullOrEmpty(queryData.SqlValue))
        {
            return queryData.Steps;
        }

        return
        [
            new QueryStepData
            {
                SqlValue = queryData.SqlValue,
                DataSourceId = queryData.DataSourceId,
                Parameters = queryData.Parameters
                    .Select(x =>
                        new QueryStepParameterData
                        {
                            Name = x.Name,
                            Placeholder = x.Placeholder
                        })
                    .ToList()
            }
        ];
    }

    // ===== ENHANCED METHODS FOR CROSS-PROJECT FUNCTIONALITY =====

    public Task<QueryExecutionResult> ExecuteQueryAdvanced(int queryId, string? finalQuery = null, List<ParameterValue>? parameters = null, CancellationToken cancellationToken = default)
    {
        return ExecuteQueryAdvancedCore(queryId, finalQuery, parameters, null, cancellationToken);
    }

    public Task<QueryExecutionResult> ExecuteQueryAdvanced(int queryId, string? finalQuery, List<ParameterValue>? parameters, int maxRows, CancellationToken cancellationToken)
    {
        return ExecuteQueryAdvancedCore(queryId, finalQuery, parameters, maxRows, cancellationToken);
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

        return (await ExecuteStep(step, parameters, null, null, cancellationToken)).Result;
    }

    /// <summary>
    /// The PII settings for work on <paramref name="dataSourceIds"/> outside any project: the global settings, tightened to
    /// the strictest live project that links any of them (<see cref="ISharedSourceSettings.GetDataSourceSettingsAsync"/>).
    /// </summary>
    private async Task<McpSettingsData> PiiSettingsAsync(IReadOnlyCollection<int> dataSourceIds, CancellationToken cancellationToken)
    {
        McpSettingsData? settings = null;
        foreach (var dataSourceId in dataSourceIds)
        {
            var sourceSettings = await sharedSourceSettings.GetDataSourceSettingsAsync(dataSourceId, cancellationToken);
            if (settings == null)
            {
                settings = sourceSettings.Clone();
                continue;
            }

            McpGuardrails.Tighten(settings, sourceSettings);
        }

        return settings ?? new McpSettingsData();
    }

    private async Task<QueryExecutionResult> ExecuteQueryAdvancedCore(int queryId, string? finalQuery, List<ParameterValue>? parameters, int? maxRows, CancellationToken cancellationToken)
    {
        var query = await GetQueryWithSteps(queryId, cancellationToken);

        // Use provided parameters or fall back to stored values from query
        var effectiveFinalQuery = finalQuery ?? query.FinalQuery;

        // All queries use the same execution path - handles single-DB, multi-step, and cross-DB!
        return await ExecuteQuerySteps(query, effectiveFinalQuery, parameters, null, null, cancellationToken, maxRows);
    }
}
