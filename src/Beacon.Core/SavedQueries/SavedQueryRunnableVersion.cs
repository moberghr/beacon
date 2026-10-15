using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.SavedQueries;

/// <summary>
/// The one rule for which version of a saved query an MCP tool may run: the query's <b>active</b> version, and only
/// when an approval request for that version was <b>Approved</b>. Nothing else is callable — not a pending or rejected
/// version, not an archived one, and not an active version that went live without review (an edit made while the
/// approval workflow is off, or a restore). In strict mode (the workflow on with
/// <see cref="ApprovalWorkflowOptions.RequireApprovedVersionToRun"/>) the approval must also have recorded the version's
/// content hash (<see cref="RequiresApprovedHash"/>): an approval recorded before Beacon kept hashes, which names a
/// version but not the content it approved, does not count until the version is approved again. Outside strict mode such
/// an approval still counts, and the host notes those tools at startup. A query with no such version is not listed as
/// a tool.
/// </summary>
/// <remarks>
/// The tool executes the approved version's snapshot (<c>QueryVersion.StepsJson</c> + <c>FinalQuery</c>), never the
/// live <c>QuerySteps</c> rows, so a later unreviewed edit to the live steps cannot change what the tool runs.
/// </remarks>
public static class SavedQueryRunnableVersion
{
    public const string NoRunnableVersionIssue =
        "It has no approved active version. Submit the query for approval and approve it first; drafts, pending and unreviewed versions never run as tools.";

    public const string NotVisibleInAnyProjectIssue =
        "It is not visible in any project: no single project contains all of its data sources, so no MCP caller can see it. Add the missing data sources to one project.";

    /// <summary>
    /// Why the runnable version cannot be a tool anyone sees, or null when it can: its shape
    /// (<see cref="SavedQueryToolRules.Inspect"/>), then its visibility (some project must hold all its data sources).
    /// </summary>
    public static async Task<string?> InspectIssueAsync(BeaconContext context, RunnableVersionInfo? runnable, CancellationToken cancellationToken)
    {
        if (runnable == null)
        {
            return NoRunnableVersionIssue;
        }

        var shape = SavedQueryToolRules.Inspect(runnable.StepsJson);
        if (shape.Issue != null)
        {
            return shape.Issue;
        }

        var dataSourceIds = shape.Steps.Select(x => x.DataSourceId);

        return await SavedQueryToolSource.IsVisibleInAnyProjectAsync(context, dataSourceIds, cancellationToken)
            ? null
            : NotVisibleInAnyProjectIssue;
    }

    /// <summary>Strict mode: the approval of a runnable version must have recorded the version's content hash.</summary>
    public static bool RequiresApprovedHash(BeaconConfiguration configuration)
    {
        return configuration.ApprovalWorkflow is { Enabled: true, RequireApprovedVersionToRun: true };
    }

    /// <param name="requireApprovedHash">Strict mode (<see cref="RequiresApprovedHash"/>): only an approval that recorded the
    /// active version's content hash counts.</param>
    public static IQueryable<Query> WhereHasRunnableVersion(this IQueryable<Query> queries, BeaconContext context, bool requireApprovedHash = false)
    {
        var active = queries
            .Where(x => x.ActiveVersionId != null)
            .Where(x => x.ActiveVersion!.Status == QueryVersionStatus.Active);

        if (requireApprovedHash)
        {
            return active
                .Where(x => context.QueryApprovalRequests
                    .Where(y => y.QueryId == x.Id)
                    .Where(y => y.QueryVersionId == x.ActiveVersionId)
                    .Where(y => y.Status == ApprovalStatus.Approved)
                    .Where(y => y.ApprovedContentHash != null)
                    .Any(y => y.ApprovedContentHash == x.ActiveVersion!.ContentHash));
        }

        return active
            .Where(x => context.QueryApprovalRequests
                .Where(y => y.QueryId == x.Id)
                .Where(y => y.QueryVersionId == x.ActiveVersionId)
                .Any(y => y.Status == ApprovalStatus.Approved));
    }

    /// <summary>
    /// Enabled tools whose active version is runnable only through an approval that recorded no content hash (approved
    /// before Beacon kept hashes): outside strict mode they run, and the host names them at startup.
    /// </summary>
    internal static IQueryable<Query> WhereToolRunsOnAnApprovalWithoutAHash(this IQueryable<Query> queries, BeaconContext context)
    {
        return queries
            .Where(x => x.McpToolEnabled)
            .Where(x => x.McpToolName != null)
            .WhereHasRunnableVersion(context)
            .Where(x => !context.QueryApprovalRequests
                .Where(y => y.QueryId == x.Id)
                .Where(y => y.QueryVersionId == x.ActiveVersionId)
                .Where(y => y.Status == ApprovalStatus.Approved)
                .Where(y => y.ApprovedContentHash != null)
                .Any(y => y.ApprovedContentHash == x.ActiveVersion!.ContentHash));
    }

    /// <summary>The runnable version of one query, or no row when it has none.</summary>
    /// <param name="requireApprovedHash">Strict mode (<see cref="RequiresApprovedHash"/>).</param>
    public static IQueryable<RunnableVersionInfo> ForQuery(BeaconContext context, int queryId, bool requireApprovedHash = false)
    {
        return context.Queries
            .Where(x => x.Id == queryId)
            .WhereHasRunnableVersion(context, requireApprovedHash)
            .Select(x =>
                new RunnableVersionInfo(
                    x.ActiveVersion!.Id,
                    x.ActiveVersion.VersionNumber,
                    x.ActiveVersion.StepsJson));
    }
}

public sealed record RunnableVersionInfo(int VersionId, int VersionNumber, string StepsJson);
