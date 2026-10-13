using Beacon.Core.Data.Entities;

namespace Beacon.Core.Models.Queries;

/// <summary>
/// Who asks for a query change and why, as an approval request records it (<c>IQueryVersionService.SubmitForApprovalAsync</c>).
/// </summary>
/// <param name="RequesterUserId">The requester's <c>Users.ExternalId</c> (<c>BeaconActor.UserId</c>); an AI actor's creator for
/// its changes. A submission without one is refused unless the host allows self-approval: nobody could approve it.</param>
/// <param name="RequesterDisplayName">The name to record when the requester has no stored user (a host without Beacon user
/// management): the display name the requester's own session presents, never another person's. A stored user is named by
/// their label.</param>
/// <param name="Source">What made the change: <c>UserEdit</c>, <c>Restore</c>, <c>AiActor</c>.</param>
/// <param name="Reason">Recorded on the proposed version.</param>
/// <param name="Summary">Recorded on the request, shown to reviewers.</param>
public sealed record QueryChangeSubmission(
    string? RequesterUserId,
    string? RequesterDisplayName,
    string Source,
    string? Reason,
    string Summary)
{
    /// <summary>
    /// The active version the change was made on, as the editor loaded it; when set and no longer the query's active
    /// version, the submission is refused (409): the editor is stale. Null: made on whatever version is active.
    /// </summary>
    public int? ExpectedBaseVersionId { get; init; }

    /// <summary>
    /// True (the default): the submission supersedes every pending request of the query. False: it supersedes only the
    /// requester's own, and is refused (409) while someone else's request waits — for a submission nobody asked to make
    /// over another person's (stored SQL brought under review by an unrelated save, an AI actor's refinement).
    /// </summary>
    public bool SupersedesOthers { get; init; } = true;

    /// <summary>
    /// Besides the requester, the people who shaped the change (<c>Users.ExternalId</c>): for an AI actor's change made by
    /// approving one of its plans, the plan's reviewer and everyone who asked for a revision of it. None of them approves
    /// the change.
    /// </summary>
    public IReadOnlyList<string> ContributorUserIds { get; init; } = [];
}

/// <summary>
/// A query change written as a version: the version (PendingApproval when submitted for approval, Active when it went
/// live), and how many pending requests of the query the submission superseded (their requesters are notified).
/// </summary>
public sealed record QueryVersionChange(QueryVersion Version, int SupersededRequests);
