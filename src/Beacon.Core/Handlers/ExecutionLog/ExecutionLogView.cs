using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Validation;

namespace Beacon.Core.Handlers.ExecutionLog;

/// <summary>
/// Which rows the execution log's read and export show, and how: every row to an Auditor, the rows of the data sources it
/// reads to anyone else; a statement still Started long after any statement could have ended is shown as
/// <see cref="QueryExecutionLogStatus.Abandoned"/> (nothing is written), and the SQL text has the values written into it
/// masked (<see cref="SqlCommentStripper.MaskLiterals"/>; the stored text is unchanged).
/// </summary>
internal static class ExecutionLogView
{
    /// <summary>
    /// Whose statements the caller reads: an Auditor (an Admin holds every permission) every row, data source or none, as
    /// it reads the security audit trail (the permission read live, never granting project data elsewhere); anyone else,
    /// a SecurityAdmin without the Auditor permission included, the rows of the data sources it may read.
    /// </summary>
    public static async Task<ProjectSet> ReadableAsync(
        IProjectAccessEvaluator projectAccess,
        IBeaconPermissionAccessor permissionAccessor,
        CancellationToken cancellationToken)
    {
        var permissions = await permissionAccessor.GetCurrentAsync(cancellationToken);
        if (permissions.Holds(BeaconPermissions.Auditor))
        {
            return ProjectSet.Unrestricted;
        }

        var scope = await projectAccess.GetScopeAsync(cancellationToken);

        return scope.For(ProjectAction.Read);
    }

    /// <summary>What is added to the statement timeout before a Started row counts as abandoned: the time a row's end
    /// takes to be written, and the shorter timeouts of the paths that set their own.</summary>
    internal static readonly TimeSpan AbandonedMargin = TimeSpan.FromMinutes(10);

    /// <summary>A Started row created before this instant is shown as abandoned.</summary>
    public static DateTime AbandonedBefore(TimeProvider timeProvider, QueryExecutionOptions queryOptions) =>
        timeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(queryOptions.StatementTimeoutSeconds) - AbandonedMargin;

    /// <summary>The rows whose shown status is <paramref name="status"/>; every row when null.</summary>
    public static IQueryable<ManualQueryExecutionLog> WhereShownStatus(
        this IQueryable<ManualQueryExecutionLog> source,
        QueryExecutionLogStatus? status,
        DateTime abandonedBefore)
    {
        return status switch
        {
            null => source,
            QueryExecutionLogStatus.Started => source
                .Where(x => x.Status == QueryExecutionLogStatus.Started)
                .Where(x => x.CreatedTime >= abandonedBefore),
            QueryExecutionLogStatus.Abandoned => source
                .Where(x => x.Status == QueryExecutionLogStatus.Started)
                .Where(x => x.CreatedTime < abandonedBefore),
            _ => source.Where(x => x.Status == status)
        };
    }

    /// <summary>The statement as readers see it: its string literals and numbers replaced by <c>?</c> and its comments
    /// removed, read in the data source's dialect (conservatively when the source or its dialect is unknown).</summary>
    public static string MaskedQueryText(string queryText, DataSourceType? dataSourceType, DatabaseEngineType? engineType)
    {
        var dialect = dataSourceType is { } type ? DataSourceSqlDialect.Of(type, engineType) : null;

        return SqlCommentStripper.MaskLiterals(queryText, dialect);
    }
}
