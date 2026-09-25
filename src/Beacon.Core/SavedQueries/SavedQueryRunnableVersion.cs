using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.SavedQueries;

/// <summary>
/// The one rule for which version of a saved query an MCP tool may run: the query's <b>active</b> version, and only
/// when an approval request for exactly that version was <b>Approved</b>. Nothing else is callable — not a pending
/// or rejected version, not an archived one, and not an active version that went live without review (an edit made
/// while the approval workflow is off, or a restore). A query with no such version is not listed as a tool.
/// </summary>
/// <remarks>
/// The tool executes the approved version's snapshot (<c>QueryVersion.StepsJson</c> + <c>FinalQuery</c>), never the
/// live <c>QuerySteps</c> rows, so a later unreviewed edit to the live steps cannot change what the tool runs.
/// </remarks>
public static class SavedQueryRunnableVersion
{
    public const string NoRunnableVersionIssue =
        "It has no approved active version. Submit the query for approval and approve it first; drafts, pending and unreviewed versions never run as tools.";

    public static IQueryable<Query> WhereHasRunnableVersion(this IQueryable<Query> queries, BeaconContext context)
    {
        return queries
            .Where(x => x.ActiveVersionId != null)
            .Where(x => x.ActiveVersion!.Status == QueryVersionStatus.Active)
            .Where(x => context.QueryApprovalRequests
                .Where(y => y.QueryId == x.Id)
                .Where(y => y.QueryVersionId == x.ActiveVersionId)
                .Any(y => y.Status == ApprovalStatus.Approved));
    }

    /// <summary>The runnable version of one query, or no row when it has none.</summary>
    public static IQueryable<RunnableVersionInfo> ForQuery(BeaconContext context, int queryId)
    {
        return context.Queries
            .Where(x => x.Id == queryId)
            .WhereHasRunnableVersion(context)
            .Select(x =>
                new RunnableVersionInfo(
                    x.ActiveVersion!.Id,
                    x.ActiveVersion.VersionNumber,
                    x.ActiveVersion.StepsJson));
    }
}

public sealed record RunnableVersionInfo(int VersionId, int VersionNumber, string StepsJson);
