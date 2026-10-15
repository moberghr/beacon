using Beacon.Core.Helpers;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Microsoft.Extensions.Logging;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Services;

public interface IQueryApprovalService
{
    /// <summary>
    /// The pending approval requests a caller may read (<paramref name="readable"/>): those whose query one readable
    /// project holds every source of, and whose version's recorded sources are readable the same way.
    /// </summary>
    Task<PagedList<ApprovalRequestSummary>> GetPendingApprovalsAsync(ListRequest request, int? queryId, ProjectSet readable, CancellationToken cancellationToken);
    /// <summary>
    /// An approval request with its proposed and active versions, the step-by-step diff between them, the proposed
    /// content's hash (what an approval names), and, for the current caller, whether it is their own request and whether
    /// they may approve it now (and why not).
    /// </summary>
    Task<ApprovalRequestDetail?> GetApprovalDetailAsync(int requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a pending request as the current caller (<see cref="IBeaconActorAccessor"/>): someone other than its
    /// requester and the other people who shaped the change (an AI plan's reviewer and revision requesters) — 403
    /// otherwise, and for a request without a recorded requester, unless
    /// <see cref="ApprovalWorkflowOptions.AllowSelfApproval"/> — while the query's active version is still the one the
    /// change was made on, for exactly the content submitted (its hash) and shown to the reviewer
    /// (<paramref name="expectedContentHash"/>, the <see cref="ApprovalRequestDetail.ContentHash"/> they reviewed:
    /// required), with the steps' orders and every step passing the save check again (409 otherwise). A request recorded
    /// before Beacon kept a change's base and content is rejected, not approved (409). The transaction takes the query's
    /// row first (as a submission does), then decides, moves the query to the proposal and writes its steps, all committed
    /// together; a request that is no longer pending answers 409.
    /// </summary>
    Task<QueryApprovalDecision> ApproveAsync(int requestId, string? comment, string? expectedContentHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rejects a pending request as the current caller (the requester may withdraw their own); a request that is no
    /// longer pending answers 409. The transaction takes the query's row first, as approving and submitting do. Only a
    /// pending proposal is marked Rejected, never a live version.
    /// </summary>
    Task<QueryApprovalDecision> RejectAsync(int requestId, string? comment, CancellationToken cancellationToken = default);
    Task<int> GetPendingCountAsync(CancellationToken cancellationToken = default);
}

internal class QueryApprovalService(
    IDbContextFactory<BeaconContext> contextFactory,
    IQueryVersionService queryVersionService,
    IBeaconActorAccessor actorAccessor,
    IBeaconUserContext userContext,
    IStepSqlSaveCheck saveCheck,
    BeaconConfiguration configuration,
    ISecurityAuditRecorder securityAudit,
    ILogger<QueryApprovalService> logger) : IQueryApprovalService
{
    /// <summary>Why a decision on a request that is no longer pending is refused.</summary>
    internal const string AlreadyDecidedMessage = "This approval request is no longer pending: it was decided or a newer submission superseded it.";

    /// <summary>Why an approval whose base is no longer the active version is refused.</summary>
    internal const string StaleMessage = "The query changed after this request was made: its active version is no longer the one the change was made on. Submit the change again.";

    /// <summary>Why a request recorded before Beacon kept a change's base and content is not approved.</summary>
    internal const string LegacyRequestMessage =
        "This request was made before Beacon recorded the version a change is made on and the content it proposes, so it can be rejected, not approved: submit the change again.";

    /// <summary>Why an approval that does not name the content it approves is refused.</summary>
    internal const string ContentNotNamedMessage = "An approval names the content it approves: reload the request and approve it again.";

    /// <summary>Why an approval of other content than the reviewer was shown is refused.</summary>
    internal const string NotTheReviewedContentMessage = "The proposed change is not the content you reviewed: reload the request and review it again.";

    /// <summary>Why a caller Beacon cannot name decides nothing.</summary>
    internal const string UnidentifiedReviewerMessage = "A decision on a change needs a signed-in reviewer.";

    /// <summary>Why a request without a recorded requester is not approved.</summary>
    internal const string NoRequesterMessage = "A request without a recorded requester can be rejected, not approved: submit the change again.";

    /// <summary>Why the requester does not approve their own change.</summary>
    internal const string OwnRequestMessage = "A change is approved by someone other than its requester.";

    /// <summary>Why someone who shaped a change does not approve it.</summary>
    internal const string ContributorMessage = "A change is approved by someone other than the people who shaped it.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<PagedList<ApprovalRequestSummary>> GetPendingApprovalsAsync(
        ListRequest request,
        int? queryId,
        ProjectSet readable,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Versions recorded before Beacon kept their sources are decided from their snapshots before paging, so the
        // page and its total count leave out the same unreadable requests.
        var unreadableLegacy = readable.Ids == null
            ? []
            : await UnreadableLegacyRequestIdsAsync(context, queryId, readable, cancellationToken);

        return await context.QueryApprovalRequests
            .Where(r => r.Status == ApprovalStatus.Pending)
            .Where(r => !queryId.HasValue || r.QueryId == queryId.Value)
            .WhereAllSourcesInOneProject(context, readable, x => x.Query.Steps.Select(y => y.DataSourceId))
            .WhereVersionSourcesAccessible(context, readable, x => x.QueryVersion.DataSourceIds)
            .WhereIf(unreadableLegacy.Count > 0, x => !unreadableLegacy.Contains(x.Id))
            .Select(r => new ApprovalRequestSummary
            {
                Id = r.Id,
                QueryId = r.QueryId,
                QueryName = r.Query.Name,
                VersionNumber = r.QueryVersion.VersionNumber,
                Status = r.Status,
                RequestedByUserName = r.RequestedByUserName,
                CreatedTime = r.CreatedTime,
                ChangeSummary = r.ChangeSummary
            })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdTime");
    }

    public async Task<ApprovalRequestDetail?> GetApprovalDetailAsync(int requestId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var request = await context.QueryApprovalRequests
            .Include(r => r.Query)
            .Include(r => r.QueryVersion)
            .Where(r => r.Id == requestId)
            .SingleOrDefaultAsync(cancellationToken);

        if (request == null) return null;

        var proposedDetail = ToVersionDetail(request.QueryVersion);

        // Diff against the version the query points at; older data can mark several versions Active.
        var activeVersion = await context.QueryVersions
            .Where(v => v.Id == request.Query.ActiveVersionId)
            .FirstOrDefaultAsync(cancellationToken);

        QueryVersionDetail? activeDetail = null;
        QueryVersionDiff? autoDiff = null;

        if (activeVersion != null)
        {
            activeDetail = ToVersionDetail(activeVersion);
            autoDiff = await queryVersionService.DiffVersionsAsync(activeVersion.Id, request.QueryVersionId, cancellationToken);
        }

        // What the caller may do with it, as approving decides it again: the server stays the arbiter.
        var caller = await actorAccessor.GetCurrentAsync(cancellationToken);
        var isPending = request.Status == ApprovalStatus.Pending;
        var isLegacy = request.BaseVersionId == null && request.QueryVersion.ContentHash == null;
        var isStale = isPending && !isLegacy && request.BaseVersionId != request.Query.ActiveVersionId;
        var blocked = !isPending
            ? null
            : FourEyesRefusal(caller, request.RequestedByUserId, request.ContributorUserIds ?? [])
                ?? (isLegacy ? LegacyRequestMessage : null)
                ?? (isStale ? StaleMessage : null);

        return new ApprovalRequestDetail
        {
            Id = request.Id,
            QueryId = request.QueryId,
            QueryName = request.Query.Name,
            QueryVersionId = request.QueryVersionId,
            Status = request.Status,
            RequestedByUserId = request.RequestedByUserId,
            RequestedByUserName = request.RequestedByUserName,
            ReviewedByUserName = request.ReviewedByUserName,
            ReviewedAt = request.ReviewedAt,
            ReviewComment = request.ReviewComment,
            ChangeSummary = request.ChangeSummary,
            CreatedTime = request.CreatedTime,
            ProposedVersion = proposedDetail,
            CurrentActiveVersion = activeDetail,
            AutoDiff = autoDiff,
            ContentHash = QueryContentHash.OfVersion(request.QueryVersion.ContentHash, request.QueryVersion.StepsJson, request.QueryVersion.FinalQuery),
            IsOwnRequest = caller.IsRecordedAs(request.RequestedByUserId),
            MayApprove = isPending && blocked == null,
            ApproveBlockedReason = blocked,
            IsStale = isStale
        };
    }

    public async Task<QueryApprovalDecision> ApproveAsync(int requestId, string? comment, string? expectedContentHash, CancellationToken cancellationToken = default)
    {
        var reviewer = await actorAccessor.GetCurrentAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var request = await context.QueryApprovalRequests
            .Where(x => x.Id == requestId)
            .Select(x =>
                new RequestToApprove
                {
                    QueryId = x.QueryId,
                    QueryVersionId = x.QueryVersionId,
                    Status = x.Status,
                    RequestedByUserId = x.RequestedByUserId,
                    ContributorUserIds = x.ContributorUserIds,
                    BaseVersionId = x.BaseVersionId,
                    Name = x.QueryVersion.Name,
                    Description = x.QueryVersion.Description,
                    FinalQuery = x.QueryVersion.FinalQuery,
                    StepsJson = x.QueryVersion.StepsJson,
                    ContentHash = x.QueryVersion.ContentHash,
                    ActiveVersionId = x.Query.ActiveVersionId
                })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Approval request {requestId} not found.");

        EnsurePending(request.Status);
        EnsureMayApprove(reviewer, request.RequestedByUserId, request.ContributorUserIds ?? [], requestId);

        // A request recorded before Beacon kept a change's base and content cannot be told stale, nor bound to what was
        // submitted: it is only rejected, and the change submitted again.
        if (request.BaseVersionId == null && request.ContentHash == null)
        {
            throw new BeaconConflictException(LegacyRequestMessage);
        }

        // Approval applies the change on top of the version it was made on (none, for a query that had no active
        // version): once another version went live, applying it would silently undo that one.
        if (request.BaseVersionId != request.ActiveVersionId)
        {
            throw new BeaconConflictException(StaleMessage);
        }

        // The content approved is the content submitted: the version's recorded hash binds the request to it. A version
        // recorded before Beacon kept hashes is approved as it reads now, and gets its hash with the approval.
        var snapshots = JsonSerializer.Deserialize<List<QueryStepSnapshot>>(request.StepsJson, JsonOptions) ?? [];
        var contentHash = QueryContentHash.Of(request.FinalQuery, snapshots).Hash;
        if (request.ContentHash != null && !string.Equals(request.ContentHash, contentHash, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Approval request {RequestId} refused: version {VersionId} of query {QueryId} no longer holds the content it was submitted with",
                requestId,
                request.QueryVersionId,
                request.QueryId);

            throw new BeaconConflictException("The proposed version no longer holds the content it was submitted with, so it cannot be approved.");
        }

        // The decision applies what the reviewer was shown: the hash the request's detail presented comes back with it.
        if (string.IsNullOrEmpty(expectedContentHash))
        {
            throw new BeaconConflictException(ContentNotNamedMessage);
        }

        if (!string.Equals(expectedContentHash, contentHash, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Approval request {RequestId} refused: the reviewer approved other content than version {VersionId} of query {QueryId} holds",
                requestId,
                request.QueryVersionId,
                request.QueryId);

            throw new BeaconConflictException(NotTheReviewedContentMessage);
        }

        // Checked again as a save is: the steps' orders, and each step's SQL (the gate, the host policy or the source's
        // metadata may have changed since the change was submitted).
        var orders = QueryStepOrders.RefusalFor(snapshots.Select(x => x.StepOrder));
        if (orders != null)
        {
            throw new BeaconConflictException($"This change cannot be approved: {orders}");
        }

        foreach (var step in snapshots)
        {
            var placeholders = step.Parameters
                .Select(x => x.Placeholder)
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x!)
                .ToList();
            var rejection = await saveCheck.RejectionAsync(step.DataSourceId, step.SqlValue, placeholders, cancellationToken);
            if (rejection != null)
            {
                throw new BeaconConflictException($"Step {step.StepOrder} cannot be approved: {rejection}");
            }
        }

        var reviewerName = await ChangeAuthorLabel.OfAsync(context, reviewer.UserId, userContext.DisplayName, cancellationToken);
        var reviewedAt = DateTime.UtcNow;

        // One unit of work: the decision, the query's move to the proposal and its steps commit together or not at all.
        // Through the execution strategy, so a host that enables retry-on-failure gets a retried unit; a retry starts
        // from a clean tracker.
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            await LockQueryAsync(context, request.QueryId, cancellationToken);

            // Decided only from Pending: a second decision, or one on a superseded request, changes nothing.
            var decided = await context.QueryApprovalRequests
                .Where(x => x.Id == requestId)
                .Where(x => x.Status == ApprovalStatus.Pending)
                .ExecuteUpdateAsync(
                    x => x
                        .SetProperty(y => y.Status, ApprovalStatus.Approved)
                        .SetProperty(y => y.ReviewedByUserId, reviewer.UserId)
                        .SetProperty(y => y.ReviewedByUserName, reviewerName)
                        .SetProperty(y => y.ReviewedAt, reviewedAt)
                        .SetProperty(y => y.ReviewComment, comment)
                        .SetProperty(y => y.ApprovedContentHash, contentHash),
                    cancellationToken);
            if (decided == 0)
            {
                throw new BeaconConflictException(AlreadyDecidedMessage);
            }

            // The query moves to the proposal only from the active version read above: a version that went live in the
            // meantime makes the approval stale, and the whole unit rolls back.
            var moved = await context.Queries
                .Where(x => x.Id == request.QueryId)
                .Where(x => x.ActiveVersionId == request.ActiveVersionId)
                .ExecuteUpdateAsync(
                    x => x
                        .SetProperty(y => y.ActiveVersionId, request.QueryVersionId)
                        .SetProperty(y => y.Name, request.Name)
                        .SetProperty(y => y.Description, request.Description)
                        .SetProperty(y => y.FinalQuery, request.FinalQuery)
                        .SetProperty(y => y.ModifiedByUserId, reviewer.UserId)
                        .SetProperty(y => y.ModifiedTime, (DateTime?)reviewedAt),
                    cancellationToken);
            if (moved == 0)
            {
                throw new BeaconConflictException(StaleMessage);
            }

            var liveSteps = await context.QuerySteps
                .Include(x => x.Parameters)
                .Where(x => x.QueryId == request.QueryId)
                .ToListAsync(cancellationToken);

            foreach (var step in liveSteps)
            {
                foreach (var parameter in step.Parameters.ToList())
                {
                    context.QueryStepParameters.Remove(parameter);
                }

                context.QuerySteps.Remove(step);
            }

            foreach (var snapshot in snapshots)
            {
                context.QuerySteps.Add(QueryVersionService.ToStep(request.QueryId, snapshot));
            }

            // The proposal becomes the one Active version (older data can hold several).
            var versions = await context.QueryVersions
                .Where(x => x.QueryId == request.QueryId)
                .Where(x => x.Status == QueryVersionStatus.Active || x.Id == request.QueryVersionId)
                .ToListAsync(cancellationToken);

            foreach (var version in versions)
            {
                var isProposal = version.Id == request.QueryVersionId;
                version.Status = isProposal ? QueryVersionStatus.Active : QueryVersionStatus.Archived;
                if (isProposal)
                {
                    version.ContentHash ??= contentHash;
                }
            }

            await securityAudit.SaveWithEventsAsync(
                context,
                () => [Decided(requestId, request.QueryId, request.QueryVersionId, ApprovalStatus.Approved, request.RequestedByUserId, withdrawn: false)],
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return new QueryApprovalDecision(requestId, ApprovalStatus.Approved, request.RequestedByUserId, reviewer.UserId);
    }

    public async Task<QueryApprovalDecision> RejectAsync(int requestId, string? comment, CancellationToken cancellationToken = default)
    {
        var reviewer = await actorAccessor.GetCurrentAsync(cancellationToken);
        EnsureIdentified(reviewer, requestId);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var request = await context.QueryApprovalRequests
            .Where(x => x.Id == requestId)
            .Select(x =>
                new
                {
                    x.QueryId,
                    x.Status,
                    x.QueryVersionId,
                    x.RequestedByUserId
                })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Approval request {requestId} not found.");

        EnsurePending(request.Status);

        var reviewerName = await ChangeAuthorLabel.OfAsync(context, reviewer.UserId, userContext.DisplayName, cancellationToken);
        var withdrawn = reviewer.IsRecordedAs(request.RequestedByUserId);
        var reviewedAt = DateTime.UtcNow;

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            await LockQueryAsync(context, request.QueryId, cancellationToken);

            // Decided only from Pending: rejecting never overwrites an approval, a rejection or a superseded request.
            var decided = await context.QueryApprovalRequests
                .Where(x => x.Id == requestId)
                .Where(x => x.Status == ApprovalStatus.Pending)
                .ExecuteUpdateAsync(
                    x => x
                        .SetProperty(y => y.Status, ApprovalStatus.Rejected)
                        .SetProperty(y => y.ReviewedByUserId, reviewer.UserId)
                        .SetProperty(y => y.ReviewedByUserName, reviewerName)
                        .SetProperty(y => y.ReviewedAt, reviewedAt)
                        .SetProperty(y => y.ReviewComment, comment),
                    cancellationToken);
            if (decided == 0)
            {
                throw new BeaconConflictException(AlreadyDecidedMessage);
            }

            // Only the proposal itself, while it is one: a version that is live is never marked Rejected.
            await context.QueryVersions
                .Where(x => x.Id == request.QueryVersionId)
                .Where(x => x.Status == QueryVersionStatus.PendingApproval)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, QueryVersionStatus.Rejected), cancellationToken);

            await securityAudit.SaveWithEventsAsync(
                context,
                () => [Decided(requestId, request.QueryId, request.QueryVersionId, ApprovalStatus.Rejected, request.RequestedByUserId, withdrawn)],
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return new QueryApprovalDecision(requestId, ApprovalStatus.Rejected, request.RequestedByUserId, reviewer.UserId);
    }

    public async Task<int> GetPendingCountAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.QueryApprovalRequests
            .CountAsync(r => r.Status == ApprovalStatus.Pending, cancellationToken);
    }

    // A decision as the security audit records it: the request, its query and version, the outcome, the requester by
    // Users.Id (the reviewer is the event's actor), and whether the requester withdrew their own request — never the comment.
    private static SecurityAuditEntry Decided(
        int requestId,
        int queryId,
        int versionId,
        ApprovalStatus outcome,
        string? requesterExternalId,
        bool withdrawn)
    {
        var action = outcome == ApprovalStatus.Approved ? SecurityAuditActions.QueryChangeApproved : SecurityAuditActions.QueryChangeRejected;

        return new SecurityAuditEntry(action)
            .On(SecurityAuditResources.ApprovalRequest, requestId)
            .With(new SecurityAuditDetails()
                .Add("queryId", queryId)
                .Add("versionId", versionId)
                .Add("outcome", outcome)
                .Add("withdrawn", withdrawn))
            .Naming("requesterUserId", requesterExternalId);
    }

    private static void EnsurePending(ApprovalStatus status)
    {
        if (status != ApprovalStatus.Pending)
        {
            throw new BeaconConflictException(AlreadyDecidedMessage);
        }
    }

    // A decision names its reviewer: a caller without a user id cannot make one.
    private void EnsureIdentified(BeaconActor reviewer, int requestId)
    {
        if (reviewer.UserId == null)
        {
            throw AccessRefusal.Of(logger, reviewer, "approval request", requestId, UnidentifiedReviewerMessage);
        }
    }

    // The query's row first, in every unit that submits or decides a change of it (a no-op update, which takes the row's
    // lock on both providers until the transaction ends): submissions and decisions on one query run one after the other,
    // in the same lock order, instead of interleaving or deadlocking. A query that is gone is decided by the statements
    // that follow.
    private static Task<int> LockQueryAsync(BeaconContext context, int queryId, CancellationToken cancellationToken)
    {
        return context.Queries
            .Where(x => x.Id == queryId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.ActiveVersionId, y => y.ActiveVersionId), cancellationToken);
    }

    private void EnsureMayApprove(BeaconActor reviewer, string? requestedByUserId, IReadOnlyList<string> contributorUserIds, int requestId)
    {
        var refusal = FourEyesRefusal(reviewer, requestedByUserId, contributorUserIds);
        if (refusal != null)
        {
            throw AccessRefusal.Of(logger, reviewer, "approval request", requestId, refusal);
        }
    }

    // Four eyes: the requester never approves their own change, nor does anyone else who shaped it (an AI plan's reviewer
    // and revision requesters), whichever way either of them signed in (a recorded id is compared with every id the
    // reviewer's request presents, so requests recorded by earlier versions are covered). A request without a recorded
    // requester cannot be told apart from the reviewer's own, so it is only rejected. Null when the reviewer may approve.
    private string? FourEyesRefusal(BeaconActor reviewer, string? requestedByUserId, IReadOnlyList<string> contributorUserIds)
    {
        if (reviewer.UserId == null)
        {
            return UnidentifiedReviewerMessage;
        }

        if (configuration.ApprovalWorkflow.AllowSelfApproval)
        {
            return null;
        }

        if (requestedByUserId == null)
        {
            return NoRequesterMessage;
        }

        if (reviewer.IsRecordedAs(requestedByUserId))
        {
            return OwnRequestMessage;
        }

        return contributorUserIds.Any(reviewer.IsRecordedAs) ? ContributorMessage : null;
    }

    private static QueryVersionDetail ToVersionDetail(QueryVersion version)
    {
        var steps = JsonSerializer.Deserialize<List<QueryStepSnapshot>>(version.StepsJson, JsonOptions) ?? [];

        return new QueryVersionDetail
        {
            Id = version.Id,
            VersionNumber = version.VersionNumber,
            Label = version.Label,
            Status = version.Status,
            Name = version.Name,
            Description = version.Description,
            FinalQuery = version.FinalQuery,
            CreatedTime = version.CreatedTime,
            ChangeSource = version.ChangeSource,
            ChangeReason = version.ChangeReason,
            Steps = steps
        };
    }

    // The pending requests (that the query filter keeps) whose version predates recorded sources and whose snapshot's
    // sources the caller may not read, or whose snapshot cannot be read: one read of the snapshots and at most one of
    // the links, whatever the number of requests.
    private static async Task<List<int>> UnreadableLegacyRequestIdsAsync(
        BeaconContext context,
        int? queryId,
        ProjectSet readable,
        CancellationToken cancellationToken)
    {
        var legacy = await context.QueryApprovalRequests
            .Where(x => x.Status == ApprovalStatus.Pending)
            .Where(x => !queryId.HasValue || x.QueryId == queryId.Value)
            .Where(x => x.QueryVersion.DataSourceIds == null)
            .WhereAllSourcesInOneProject(context, readable, x => x.Query.Steps.Select(y => y.DataSourceId))
            .Select(x =>
                new
                {
                    x.Id,
                    x.QueryVersion.StepsJson
                })
            .ToListAsync(cancellationToken);
        if (legacy.Count == 0)
        {
            return [];
        }

        var sources = legacy
            .Select(x => QueryVersionSources.Of(null, x.StepsJson) ?? [])
            .ToList();
        var reachable = await ProjectSourceFootprint.AllowEachAsync(context, readable, sources, cancellationToken);

        return legacy
            .Where((_, i) => !reachable[i])
            .Select(x => x.Id)
            .ToList();
    }

    private sealed class RequestToApprove
    {
        public int QueryId { get; init; }

        public int QueryVersionId { get; init; }

        public ApprovalStatus Status { get; init; }

        public string? RequestedByUserId { get; init; }

        public string[]? ContributorUserIds { get; init; }

        public int? BaseVersionId { get; init; }

        public string Name { get; init; } = string.Empty;

        public string? Description { get; init; }

        public string? FinalQuery { get; init; }

        public string StepsJson { get; init; } = string.Empty;

        public string? ContentHash { get; init; }

        public int? ActiveVersionId { get; init; }
    }
}
