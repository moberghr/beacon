using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Services;

/// <summary>
/// What the approval workflow compares a query's stored steps with: the version the query points at as active, its
/// content hash (computed from its snapshot for a version recorded before Beacon kept one) and whether an approved request
/// for exactly that content exists. With the workflow on, a subscription run executes the stored SQL only when it is
/// that version's content (<see cref="RunRefusal"/>); in strict mode
/// (<see cref="ApprovalWorkflowOptions.RequireApprovedVersionToRun"/>) the version must also be Active and approved for its
/// recorded hash, the rule saved-query MCP tools follow in strict mode — a version approved before Beacon recorded hashes is
/// not, and is approved again by restoring it. Without an active version, nothing is compared outside strict mode: a query
/// stored before the workflow was on and never versioned since runs as stored, and each such run logs a Warning (a query
/// created while the workflow is on has nothing stored to run until its first version is approved).
/// </summary>
internal static class ReviewedQueryContent
{
    /// <summary>The neutral reason a refused run gives (no detail of the change).</summary>
    public const string NotReviewedMessage = "The query's stored SQL is not its approved version.";

    public static Task<ActiveVersionContent?> ActiveVersionAsync(BeaconContext context, int queryId, CancellationToken cancellationToken)
    {
        return context.Queries
            .Where(x => x.Id == queryId)
            .Where(x => x.ActiveVersionId != null)
            .Select(x =>
                new ActiveVersionContent
                {
                    VersionId = x.ActiveVersion!.Id,
                    VersionNumber = x.ActiveVersion.VersionNumber,
                    Status = x.ActiveVersion.Status,
                    ContentHash = x.ActiveVersion.ContentHash,
                    StepsJson = x.ActiveVersion.ContentHash == null ? x.ActiveVersion.StepsJson : null,
                    FinalQuery = x.ActiveVersion.FinalQuery,
                    IsApprovedForItsHash = context.QueryApprovalRequests
                        .Where(y => y.QueryVersionId == x.ActiveVersionId)
                        .Where(y => y.Status == ApprovalStatus.Approved)
                        .Where(y => y.ApprovedContentHash != null)
                        .Any(y => y.ApprovedContentHash == x.ActiveVersion.ContentHash)
                })
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>True when <paramref name="live"/> is the content of the version the query points at as active.</summary>
    public static bool IsActiveContent(QueryContent live, ActiveVersionContent? active)
    {
        return active != null && string.Equals(active.Hash, live.Hash, StringComparison.Ordinal);
    }

    /// <summary>Why a run of <paramref name="live"/> is refused, or null when it may run.</summary>
    public static string? RunRefusal(QueryContent live, ActiveVersionContent? active, bool strict)
    {
        if (active == null)
        {
            return strict ? NotReviewedMessage : null;
        }

        if (!IsActiveContent(live, active))
        {
            return NotReviewedMessage;
        }

        if (strict && (active.Status != QueryVersionStatus.Active || active.ContentHash == null || !active.IsApprovedForItsHash))
        {
            return NotReviewedMessage;
        }

        return null;
    }
}

/// <summary>The active version of a query, as <see cref="ReviewedQueryContent"/> reads it.</summary>
internal sealed record ActiveVersionContent
{
    public int VersionId { get; init; }

    public int VersionNumber { get; init; }

    public QueryVersionStatus Status { get; init; }

    /// <summary>The stored hash; null for a version recorded before Beacon kept it.</summary>
    public string? ContentHash { get; init; }

    /// <summary>The snapshot, read only when there is no stored hash to compute it from.</summary>
    public string? StepsJson { get; init; }

    public string? FinalQuery { get; init; }

    /// <summary>An approved request for this version recorded the version's stored hash as the content it approved.</summary>
    public bool IsApprovedForItsHash { get; init; }

    /// <summary>The version's content hash: stored, or computed from its snapshot; null when the snapshot cannot be read.</summary>
    public string? Hash => QueryContentHash.OfVersion(ContentHash, StepsJson ?? string.Empty, FinalQuery);
}

/// <summary>
/// A subscription run refused because, with the approval workflow on, its query's stored SQL is not the reviewed version
/// (<see cref="ReviewedQueryContent.RunRefusal"/>). A scheduled run is recorded as skipped; a run a request asks for is
/// recorded the same way and answered 409.
/// </summary>
internal sealed class QueryNotReviewedException(IReadOnlyList<int> dataSourceIds)
    : BeaconException(ReviewedQueryContent.NotReviewedMessage)
{
    public IReadOnlyList<int> DataSourceIds { get; } = dataSourceIds;
}
