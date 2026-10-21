using Beacon.Core.Data.Entities;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;

namespace Beacon.Core.Services;

/// <summary>
/// Checks a proposed query without running any of it, for SQL that may not run before a second person reviewed it (an AI
/// actor's new query or refinement while the approval workflow is on). Each step passes the checks of a save — blocked
/// keywords, then <see cref="IStepSqlSaveCheck"/>: the execution gate in its source's dialect as the step would run (the
/// regex guardrail, one read-only statement, names in the source's own configured database, the host policy) and the
/// source's metadata — and then the source's own non-executing validation
/// (<see cref="IDataSourceProvider.ValidateQueryAsync"/>): <c>EXPLAIN</c> without <c>ANALYZE</c> on PostgreSQL, MySQL and
/// Snowflake, <c>sp_describe_first_result_set</c> on SQL Server and Azure Synapse, which plan the statement or describe its
/// result and return none of its rows; a source without such a validation (SQLite) is left to the gate. The final query
/// passes the gate in the form that joins the step results. Registered with <c>TryAdd</c>.
/// </summary>
public interface IQueryProposalCheck
{
    /// <returns>Null when the proposal passes; the reason it does not otherwise.</returns>
    Task<string?> RejectionAsync(QueryDraft proposal, CancellationToken cancellationToken);
}

internal partial class QueryService : IQueryProposalCheck
{
    public async Task<string?> RejectionAsync(QueryDraft proposal, CancellationToken cancellationToken)
    {
        try
        {
            ValidateFinalQuery(proposal.FinalQuery);

            var steps = await BuildDraftSteps(0, proposal.Steps, cancellationToken);
            await EnsureStepsPassTheGateAsync(proposal.Steps, cancellationToken);

            foreach (var step in steps)
            {
                var rowSecurity = await rowSecurityResolver.ResolveAsync(step.DataSourceId, cancellationToken);
                var failure = await sqlRunner.ValidationFailureAsync(step.DataSource, DryRunSql(step), rowSecurity, cancellationToken);
                if (failure != null)
                {
                    return failure;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or BeaconException)
        {
            return ex.Message;
        }
    }

    // The text a step's dry run validates: each placeholder written as NULL, as the save check's catalog stage judges the
    // step (a bind marker without its value reads as a name on some engines), flattened as the step runs.
    private static string DryRunSql(QueryStep step)
    {
        var sql = step.Parameters
            .Select(x => x.Placeholder)
            .Where(x => !string.IsNullOrEmpty(x))
            .Aggregate(step.SqlValue, (current, placeholder) => current.Replace(placeholder!, "NULL"));

        return StoredSql.Flatten(sql, DataSourceSqlDialect.Of(step.DataSource));
    }
}
