using System.Text.Json;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// Change requests of migration jobs, data contracts' Custom SQL rules, recipients, data-source connections and the LLM
/// connection: the pending ones, one with what it changes, and the decisions on them. The same rules as query approvals:
/// the reviewer is the caller (<see cref="IBeaconActorAccessor"/>) and never the requester (unless
/// <see cref="ApprovalWorkflowOptions.AllowSelfApproval"/>; a request without a recorded requester is only rejected); a
/// request is decided once, from Pending, by a conditional update in a transaction that takes the resource's row first; an
/// approval names the payload the reviewer was shown and applies only while the resource still holds exactly the
/// definition the request records. An Approver reads and decides every request but the LLM connection's, which only a
/// live Admin reads and decides (the requester included); anyone else reads, like query approvals, the requests of the
/// migration jobs and data contracts it reads (<see cref="ChangeApprovalScope"/>), never a recipient's or a connection's.
/// </summary>
public interface IChangeApprovalService
{
    /// <summary>
    /// The pending change requests <paramref name="scope"/> reads, newest first unless <c>sort</c> says otherwise; of one
    /// kind when <paramref name="kind"/> is given.
    /// </summary>
    Task<PagedList<ChangeApprovalSummary>> GetPendingAsync(ListRequest request, ChangeApprovalKind? kind, ChangeApprovalScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// A change request with what it changes, and whether the caller may approve it now (and why not); null when it does
    /// not exist or <paramref name="scope"/> does not read it.
    /// </summary>
    Task<ChangeApprovalDetail?> GetDetailAsync(int requestId, ChangeApprovalScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Approves a pending request as the caller: 403 for a caller who is no live Admin on an LLM connection change, for its
    /// requester (by any id the caller presents) or a caller without a user id, and for a request without a recorded
    /// requester; 409 when it is no longer pending, when a data source's server is not the engine it declares (the request
    /// stays pending), when
    /// <paramref name="expectedPayloadHash"/> is not the payload the detail shows, when what it proposes cannot be read,
    /// when the resource no longer exists or no longer holds exactly the definition the request records; 400 without a
    /// comment when the change lies only in a masked part, or in a connection shown only by its fingerprint
    /// (<see cref="ChangeApprovalDetail.CommentRequiredToApprove"/>). The resource then works with that definition. An LLM
    /// connection change is also refused (409) when it waited past its expiry or the stored connection changed since it
    /// was made (both supersede it and wipe what it staged), or when what it staged cannot be read, and (400) when its
    /// provider is not installed (the request stays pending).
    /// </summary>
    Task<ChangeApprovalDecision> ApproveAsync(int requestId, string? comment, string? expectedPayloadHash, CancellationToken cancellationToken);

    /// <summary>
    /// Rejects a pending request as the caller (its requester may withdraw it); 403 for a caller who is no live Admin on an
    /// LLM connection change, the requester included; 409 when it is no longer pending, or is an LLM connection change past
    /// its expiry. The resource keeps the definition it holds, which stays inactive until it is changed back or changed and
    /// approved; a rejected LLM connection change's staged connection is wiped.
    /// </summary>
    Task<ChangeApprovalDecision> RejectAsync(int requestId, string? comment, CancellationToken cancellationToken);
}

internal sealed class ChangeApprovalService(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    IBeaconUserContext userContext,
    RecipientSecretProtector secretProtector,
    RecipientDestinationHasher hasher,
    BeaconConfiguration configuration,
    ISecurityAuditRecorder securityAudit,
    IBeaconPermissionAccessor permissionAccessor,
    IDataSourceDefinitionReader definitionReader,
    IHeldDataSourceProbe heldDataSourceProbe,
    LlmConnectionApproval llmConnections,
    ILogger<ChangeApprovalService> logger,
    IApprovalNotifier? approvalNotifier = null) : IChangeApprovalService
{
    /// <summary>Why an approval of a resource that changed since the request is refused.</summary>
    internal const string ChangedSinceSubmittedMessage =
        "The change can no longer be approved: what it changes was changed again after this request was made. Submit the change again.";

    /// <summary>Why an approval of a resource that no longer exists is refused.</summary>
    internal const string ResourceGoneMessage = "What this request changes no longer exists: the request can be rejected, not approved.";

    /// <summary>Why an approval of a recipient whose stored destination cannot be read is refused.</summary>
    internal const string UnreadableMessage = "The recipient's stored destination cannot be read: enter it again, which submits it for approval.";

    /// <summary>Why a request whose proposed change cannot be read is not approved.</summary>
    internal const string UnreadableProposalMessage =
        "What this request proposes cannot be shown, so it cannot be approved: reject it, and submit the change again.";

    /// <summary>Why approving a change that lies only in a masked part needs a comment.</summary>
    internal const string CommentRequiredMessage =
        "The change lies only in a masked part (a token, signature or header value): say in a comment how you checked it, for example by comparing the fingerprints with the requester.";

    /// <summary>Why approving a data-source connection its connector cannot show setting by setting needs a comment.</summary>
    internal const string ConnectionCommentRequiredMessage =
        "This connection can only be shown by its fingerprint: say in a comment how you checked it, for example by comparing the fingerprint with the requester.";

    /// <summary>Why an approval of a data source whose stored connection cannot be read is refused.</summary>
    internal const string UnreadableConnectionMessage =
        "The data source's stored connection cannot be read: save its connection again, which submits it for approval.";

    /// <summary>Why a caller who is no live Admin cannot decide a change of the LLM connection.</summary>
    internal const string LiveAdminRequiredMessage = "Only an Admin decides a change of the LLM connection.";

    /// <summary>Why an approval of the LLM connection that was committed but not applied in this process is not a plain success.</summary>
    internal const string ApprovedNotAppliedMessage =
        "The change of the LLM connection was approved and stored, but Beacon could not apply it here: the previous connection stays in use until Beacon restarts.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<PagedList<ChangeApprovalSummary>> GetPendingAsync(ListRequest request, ChangeApprovalKind? kind, ChangeApprovalScope scope, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Only a live Admin lists the LLM connection's requests: those that waited past their expiry go first, wiped.
        if (scope.IsApprover && scope.IsLiveAdmin && kind is null or ChangeApprovalKind.LlmConnection)
        {
            await llmConnections.ExpireAsync(context, cancellationToken);
        }

        return await Readable(context, scope)
            .Where(x => x.Status == ApprovalStatus.Pending)
            .WhereIf(kind.HasValue, x => x.Kind == kind)
            .Select(x =>
                new ChangeApprovalSummary
                {
                    Id = x.Id,
                    Kind = x.Kind,
                    ResourceId = x.ResourceId,
                    ResourceName = x.ResourceName,
                    Status = x.Status,
                    RequestedByUserName = x.RequestedByUserName,
                    CreatedTime = x.CreatedTime,
                    ChangeSummary = x.ChangeSummary
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdTime");
    }

    public async Task<ChangeApprovalDetail?> GetDetailAsync(int requestId, ChangeApprovalScope scope, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Only what the detail shows: a staged LLM connection's encrypted values are never read here.
        var request = await Readable(context, scope)
            .Where(x => x.Id == requestId)
            .Select(x =>
                new RequestDetail
                {
                    Id = x.Id,
                    Kind = x.Kind,
                    ResourceId = x.ResourceId,
                    ResourceName = x.ResourceName,
                    Status = x.Status,
                    RequestedByUserId = x.RequestedByUserId,
                    RequestedByUserName = x.RequestedByUserName,
                    ReviewedByUserId = x.ReviewedByUserId,
                    ReviewedByUserName = x.ReviewedByUserName,
                    ReviewedAt = x.ReviewedAt,
                    ReviewComment = x.ReviewComment,
                    ChangeSummary = x.ChangeSummary,
                    CreatedTime = x.CreatedTime,
                    DefinitionHash = x.DefinitionHash,
                    PayloadHash = x.PayloadHash,
                    ProposedJson = x.ProposedJson,
                    PreviousJson = x.PreviousJson
                })
            .FirstOrDefaultAsync(cancellationToken);
        if (request == null)
        {
            return null;
        }

        // A staged LLM connection that waited past its expiry is wiped as it is read.
        var status = request.Status;
        if (request.Kind == ChangeApprovalKind.LlmConnection && status == ApprovalStatus.Pending && llmConnections.IsExpired(request.CreatedTime))
        {
            await llmConnections.ExpireAsync(context, cancellationToken);
            status = ApprovalStatus.Superseded;
        }

        var state = await StateAsync(context, request.Kind, request.ResourceId, request.DefinitionHash, cancellationToken);
        var caller = await actorAccessor.GetCurrentAsync(cancellationToken);
        var isPending = status == ApprovalStatus.Pending;
        var staleReason = StaleReason(request.Kind, state, request.DefinitionHash);
        var unreadable = !ProposalReadable(request.Kind, request.ProposedJson);
        var blocked = isPending
            ? FourEyesRefusal(caller, request.RequestedByUserId) ?? (unreadable ? UnreadableProposalMessage : null) ?? staleReason
            : null;
        var names = await DataSourceNamesAsync(context, request.Kind, request.ProposedJson, request.PreviousJson, cancellationToken);
        var previousDestination = Read<RecipientDestinationView>(request.Kind, request.PreviousJson, ChangeApprovalKind.RecipientDestination);
        var proposedDestination = Read<RecipientDestinationView>(request.Kind, request.ProposedJson, ChangeApprovalKind.RecipientDestination);
        var hiddenByMask = RecipientDestinationDefinition.ChangeHiddenByMask(previousDestination, proposedDestination);
        var proposedConnection = Read<DataSourceConnectionView>(request.Kind, request.ProposedJson, ChangeApprovalKind.DataSourceConnection);
        var previousLlm = Read<LlmConnectionView>(request.Kind, request.PreviousJson, ChangeApprovalKind.LlmConnection);
        var proposedLlm = Read<LlmConnectionView>(request.Kind, request.ProposedJson, ChangeApprovalKind.LlmConnection);

        var reviewerId = request.ReviewedByUserId == null
            ? null
            : await context.Users
                .Where(x => x.ExternalId == request.ReviewedByUserId)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(cancellationToken);

        return new ChangeApprovalDetail
        {
            Id = request.Id,
            Kind = request.Kind,
            ResourceId = request.ResourceId,
            ResourceName = state.Name ?? request.ResourceName,
            ResourceExists = state.Exists,
            Status = status,
            RequestedByUserName = request.RequestedByUserName,
            ReviewedByUserName = request.ReviewedByUserName,
            ReviewedByBeaconUserId = reviewerId,
            ReviewedAt = request.ReviewedAt,
            ReviewComment = request.ReviewComment,
            ChangeSummary = request.ChangeSummary,
            CreatedTime = request.CreatedTime,
            PreviousMigrationJob = Named(Read<MigrationJobDefinitionView>(request.Kind, request.PreviousJson, ChangeApprovalKind.MigrationJob), names),
            ProposedMigrationJob = Named(Read<MigrationJobDefinitionView>(request.Kind, request.ProposedJson, ChangeApprovalKind.MigrationJob), names),
            PreviousCustomSqlRules = Named(Read<CustomSqlRulesView>(request.Kind, request.PreviousJson, ChangeApprovalKind.CustomSqlRules), names),
            ProposedCustomSqlRules = Named(Read<CustomSqlRulesView>(request.Kind, request.ProposedJson, ChangeApprovalKind.CustomSqlRules), names),
            PreviousRecipientDestination = previousDestination,
            ProposedRecipientDestination = proposedDestination,
            PreviousDataSourceConnection = Read<DataSourceConnectionView>(request.Kind, request.PreviousJson, ChangeApprovalKind.DataSourceConnection),
            ProposedDataSourceConnection = proposedConnection,
            PreviousLlmConnection = previousLlm,
            ProposedLlmConnection = proposedLlm,
            CurrentProjects = await CurrentProjectsAsync(context, request.Kind, request.ResourceId, cancellationToken),
            PayloadHash = request.PayloadHash,
            IsOwnRequest = caller.IsRecordedAs(request.RequestedByUserId),
            MayApprove = isPending && blocked == null,
            ApproveBlockedReason = blocked,
            IsStale = isPending && staleReason != null,
            ChangeHiddenByMask = hiddenByMask,
            CommentRequiredToApprove = isPending
                && (hiddenByMask || proposedConnection?.SecretOnlyDefinition == true || LlmConnectionApproval.ChangeOnlyInFingerprints(previousLlm, proposedLlm))
        };
    }

    public async Task<ChangeApprovalDecision> ApproveAsync(int requestId, string? comment, string? expectedPayloadHash, CancellationToken cancellationToken)
    {
        var reviewer = await actorAccessor.GetCurrentAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var request = await RequestToDecideAsync(context, requestId, cancellationToken);

        EnsurePending(request.Status);
        await EnsureKindDecidableAsync(reviewer, request.Kind, requestId, cancellationToken);
        await RefuseIfExpiredAsync(context, request, requestId, cancellationToken);
        var refusal = FourEyesRefusal(reviewer, request.RequestedByUserId);
        if (refusal != null)
        {
            throw AccessRefusal.Of(logger, reviewer, "change request", requestId, refusal);
        }

        // The decision applies what the reviewer was shown: the payload hash the request's detail presented comes back.
        if (string.IsNullOrEmpty(expectedPayloadHash))
        {
            throw new BeaconConflictException(QueryApprovalService.ContentNotNamedMessage);
        }

        if (!string.Equals(expectedPayloadHash, request.PayloadHash, StringComparison.Ordinal))
        {
            logger.LogWarning("Change request {RequestId} refused: the reviewer approved another payload than the request holds", requestId);

            throw new BeaconConflictException(QueryApprovalService.NotTheReviewedContentMessage);
        }

        // Nothing is approved that the reviewer could not be shown.
        if (!ProposalReadable(request.Kind, request.ProposedJson))
        {
            logger.LogWarning("Change request {RequestId} refused: what it proposes cannot be read", requestId);

            throw new BeaconConflictException(UnreadableProposalMessage);
        }

        // A change that lies only in a masked part is approved with a comment saying how it was checked.
        if (string.IsNullOrWhiteSpace(comment)
            && RecipientDestinationDefinition.ChangeHiddenByMask(
                Read<RecipientDestinationView>(request.Kind, request.PreviousJson, ChangeApprovalKind.RecipientDestination),
                Read<RecipientDestinationView>(request.Kind, request.ProposedJson, ChangeApprovalKind.RecipientDestination)))
        {
            throw new InvalidOperationException(CommentRequiredMessage);
        }

        if (string.IsNullOrWhiteSpace(comment)
            && Read<DataSourceConnectionView>(request.Kind, request.ProposedJson, ChangeApprovalKind.DataSourceConnection) is { SecretOnlyDefinition: true })
        {
            throw new InvalidOperationException(ConnectionCommentRequiredMessage);
        }

        if (string.IsNullOrWhiteSpace(comment)
            && LlmConnectionApproval.ChangeOnlyInFingerprints(
                Read<LlmConnectionView>(request.Kind, request.PreviousJson, ChangeApprovalKind.LlmConnection),
                Read<LlmConnectionView>(request.Kind, request.ProposedJson, ChangeApprovalKind.LlmConnection)))
        {
            throw new InvalidOperationException(LlmConnectionApproval.CommentRequiredMessage);
        }

        // A held data source connected to nothing: its server is checked once, before anything is decided, and a server that
        // is not the declared engine refuses the approval (409), leaving the request pending.
        if (request.Kind == ChangeApprovalKind.DataSourceConnection)
        {
            // A source that no longer holds the definition the request proposes is refused before its server is dialled
            // (the probe would connect with a connection nobody reviewed); the check under the lock below stays the one
            // that decides.
            await heldDataSourceProbe.ProbeAsync(request.ResourceId, request.DefinitionHash, cancellationToken);
        }

        // A staged LLM connection is read and checked once, before anything is decided: one that cannot be read, is not the
        // one shown or names a provider not installed refuses the approval, leaving the request pending.
        var llmProposal = request.Kind == ChangeApprovalKind.LlmConnection
            ? await llmConnections.ProposalToApplyAsync(context, requestId, request.ProposedJson, cancellationToken)
            : null;

        var reviewerName = await ChangeAuthorLabel.OfAsync(context, reviewer.UserId, userContext.DisplayName, cancellationToken);
        var reviewedAt = DateTime.UtcNow;

        // One unit of work: the resource's row first (as a submission takes it), the definition it holds checked under that
        // lock, the decision from Pending and the approved definition, committed together or not at all. Through the
        // execution strategy, so a host that enables retry-on-failure gets a retried unit; a retry starts from a clean
        // tracker.
        StagedAppSettings? appliedSettings = null;
        var stale = false;
        var strategy = context.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                var locked = await LockResourceAsync(context, request.Kind, request.ResourceId, cancellationToken);
                if (locked == 0)
                {
                    throw new BeaconConflictException(ResourceGoneMessage);
                }

                var state = await StateAsync(context, request.Kind, request.ResourceId, request.DefinitionHash, cancellationToken);
                var staleReason = StaleReason(request.Kind, state, request.DefinitionHash);
                if (staleReason != null)
                {
                    logger.LogWarning(
                        "Change request {RequestId} refused: {Kind} {ResourceId} no longer holds the definition it proposes",
                        requestId,
                        request.Kind,
                        request.ResourceId);

                    stale = true;
                    throw new BeaconConflictException(staleReason);
                }

                // Decided only from Pending: a second decision, or one on a superseded request, changes nothing.
                var decided = await context.ChangeApprovalRequests
                    .Where(x => x.Id == requestId)
                    .Where(x => x.Status == ApprovalStatus.Pending)
                    .ExecuteUpdateAsync(
                        x => x
                            .SetProperty(y => y.Status, ApprovalStatus.Approved)
                            .SetProperty(y => y.ReviewedByUserId, reviewer.UserId)
                            .SetProperty(y => y.ReviewedByUserName, reviewerName)
                            .SetProperty(y => y.ReviewedAt, reviewedAt)
                            .SetProperty(y => y.ReviewComment, comment),
                        cancellationToken);
                if (decided == 0)
                {
                    throw new BeaconConflictException(QueryApprovalService.AlreadyDecidedMessage);
                }

                appliedSettings = await ApplyAsync(context, requestId, request, state, llmProposal, cancellationToken);
                await securityAudit.SaveWithEventsAsync(
                    context,
                    () =>
                    [
                        ChangeApprovalAuditEntries.Decided(requestId, request.Kind, request.ResourceId, ApprovalStatus.Approved, request.RequestedByUserId),
                        .. appliedSettings?.AuditEntries() ?? []
                    ],
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            });
        }
        catch (BeaconConflictException) when (stale && request.Kind == ChangeApprovalKind.LlmConnection)
        {
            // A staged LLM connection made from a connection no longer stored can never be applied: after the rollback it
            // is superseded and wiped.
            await llmConnections.SupersedeStaleAsync(context, requestId, cancellationToken);

            throw;
        }

        var applied = await AfterApprovalAsync(requestId, request, appliedSettings, cancellationToken);
        await NotifyAsync(requestId, "approved", reviewer.UserId, request.RequestedByUserId, cancellationToken);
        if (!applied)
        {
            // The decision stands; the approver is told it is not in use yet rather than shown a plain success.
            throw new BeaconConflictException(ApprovedNotAppliedMessage);
        }

        return new ChangeApprovalDecision(requestId, ApprovalStatus.Approved, request.RequestedByUserId, reviewer.UserId);
    }

    public async Task<ChangeApprovalDecision> RejectAsync(int requestId, string? comment, CancellationToken cancellationToken)
    {
        var reviewer = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (reviewer.UserId == null)
        {
            throw AccessRefusal.Of(logger, reviewer, "change request", requestId, QueryApprovalService.UnidentifiedReviewerMessage);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var request = await RequestToDecideAsync(context, requestId, cancellationToken);

        EnsurePending(request.Status);
        await EnsureKindDecidableAsync(reviewer, request.Kind, requestId, cancellationToken);
        await RefuseIfExpiredAsync(context, request, requestId, cancellationToken);

        var reviewerName = await ChangeAuthorLabel.OfAsync(context, reviewer.UserId, userContext.DisplayName, cancellationToken);
        var reviewedAt = DateTime.UtcNow;

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            // The resource's row first, as submitting and approving take it; a resource that is gone takes nothing.
            await LockResourceAsync(context, request.Kind, request.ResourceId, cancellationToken);

            // Decided only from Pending: rejecting never overwrites an approval, a rejection or a superseded request. A
            // rejected request is never applied, so a staged LLM connection is wiped with the decision.
            var decided = await context.ChangeApprovalRequests
                .Where(x => x.Id == requestId)
                .Where(x => x.Status == ApprovalStatus.Pending)
                .ExecuteUpdateAsync(
                    x => x
                        .SetProperty(y => y.Status, ApprovalStatus.Rejected)
                        .SetProperty(y => y.ReviewedByUserId, reviewer.UserId)
                        .SetProperty(y => y.ReviewedByUserName, reviewerName)
                        .SetProperty(y => y.ReviewedAt, reviewedAt)
                        .SetProperty(y => y.ReviewComment, comment)
                        .SetProperty(y => y.EncryptedPayload, (string?)null),
                    cancellationToken);
            if (decided == 0)
            {
                throw new BeaconConflictException(QueryApprovalService.AlreadyDecidedMessage);
            }

            await securityAudit.SaveWithEventsAsync(
                context,
                () => [ChangeApprovalAuditEntries.Decided(requestId, request.Kind, request.ResourceId, ApprovalStatus.Rejected, request.RequestedByUserId)],
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        await NotifyAsync(requestId, "rejected", reviewer.UserId, request.RequestedByUserId, cancellationToken);

        return new ChangeApprovalDecision(requestId, ApprovalStatus.Rejected, request.RequestedByUserId, reviewer.UserId);
    }

    private static void EnsurePending(ApprovalStatus status)
    {
        if (status != ApprovalStatus.Pending)
        {
            throw new BeaconConflictException(QueryApprovalService.AlreadyDecidedMessage);
        }
    }

    // Who decides a request of this kind, checked before the four-eyes rule: a change of the LLM connection only a live
    // Admin, whoever requested it (its requester too, so withdrawing it needs a live Admin as well).
    private async Task EnsureKindDecidableAsync(BeaconActor reviewer, ChangeApprovalKind kind, int requestId, CancellationToken cancellationToken)
    {
        var refusal = await KindRefusalAsync(kind, cancellationToken);
        if (refusal != null)
        {
            throw AccessRefusal.Of(logger, reviewer, "change request", requestId, refusal);
        }
    }

    // A staged LLM connection that waited past its expiry is not decided: superseded, wiped, 409.
    private Task RefuseIfExpiredAsync(BeaconContext context, RequestToDecide request, int requestId, CancellationToken cancellationToken)
    {
        return request.Kind == ChangeApprovalKind.LlmConnection
            ? llmConnections.RefuseIfExpiredAsync(context, requestId, cancellationToken)
            : Task.CompletedTask;
    }

    private async Task<string?> KindRefusalAsync(ChangeApprovalKind kind, CancellationToken cancellationToken)
    {
        if (kind != ChangeApprovalKind.LlmConnection)
        {
            return null;
        }

        return (await permissionAccessor.GetCurrentAsync(cancellationToken)).IsAdmin ? null : LiveAdminRequiredMessage;
    }

    private static async Task<RequestToDecide> RequestToDecideAsync(BeaconContext context, int requestId, CancellationToken cancellationToken)
    {
        return await context.ChangeApprovalRequests
            .Where(x => x.Id == requestId)
            .Select(x =>
                new RequestToDecide
                {
                    Kind = x.Kind,
                    ResourceId = x.ResourceId,
                    Status = x.Status,
                    RequestedByUserId = x.RequestedByUserId,
                    DefinitionHash = x.DefinitionHash,
                    PayloadHash = x.PayloadHash,
                    ProposedJson = x.ProposedJson,
                    PreviousJson = x.PreviousJson
                })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Change request {requestId} not found.");
    }

    // The resource's row, by a no-op update that holds its lock on both providers until the transaction ends: submissions
    // and decisions on one resource run one after the other, in the same order. An archived or deleted resource matches
    // no row.
    private static Task<int> LockResourceAsync(BeaconContext context, ChangeApprovalKind kind, int resourceId, CancellationToken cancellationToken)
    {
        return kind switch
        {
            ChangeApprovalKind.MigrationJob => context.MigrationJobs
                .Where(x => x.Id == resourceId)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.ApprovedDefinitionHash, y => y.ApprovedDefinitionHash), cancellationToken),
            ChangeApprovalKind.CustomSqlRules => context.DataContracts
                .Where(x => x.Id == resourceId)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.IsEnabled, y => y.IsEnabled), cancellationToken),
            ChangeApprovalKind.RecipientDestination => context.Recipients
                .Where(x => x.Id == resourceId)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.ApprovedDestinationHash, y => y.ApprovedDestinationHash), cancellationToken),
            ChangeApprovalKind.DataSourceConnection => DataSourceService.LockAsync(context, resourceId, cancellationToken),
            // One stored LLM connection, no row to take: a second pending request is refused by the database's unique index,
            // and a change made since the request by the check of the connection it was based on.
            ChangeApprovalKind.LlmConnection => Task.FromResult(1),
            _ => Task.FromResult(0)
        };
    }

    // The resource as it is now: whether it exists, its name, and the hash of the definition a request of it proposes (null
    // when it cannot be read). For a data contract, the hash of its Custom SQL rules waiting for approval, and those rules.
    // A recipient's keyed hash may have been recorded under a previous key: one its definition matches under any key is
    // reported as the requested hash, and an approval records it under the current key.
    private async Task<GovernedState> StateAsync(
        BeaconContext context,
        ChangeApprovalKind kind,
        int resourceId,
        string requestedHash,
        CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case ChangeApprovalKind.MigrationJob:
                {
                    var job = await context.MigrationJobs
                        .Where(x => x.Id == resourceId)
                        .Select(x =>
                            new
                            {
                                x.Name,
                                Definition = new MigrationJobDefinition(
                                    x.DataSourceId,
                                    x.QueryText,
                                    x.DestinationDataSourceId,
                                    x.DestinationTable,
                                    x.Mode,
                                    x.TransformationScript)
                            })
                        .FirstOrDefaultAsync(cancellationToken);

                    return job == null ? GovernedState.Missing : new GovernedState(true, job.Name, job.Definition.Hash, job.Definition.Hash, []);
                }

            case ChangeApprovalKind.CustomSqlRules:
                {
                    var contract = await context.DataContracts
                        .Where(x => x.Id == resourceId)
                        .Select(x =>
                            new
                            {
                                x.Name,
                                x.DataSourceId,
                                Rules = x.Rules
                                    .Where(y => y.RuleType == DataContractRuleType.CustomSql)
                                    .Where(y => y.ApprovedSqlHash != null)
                                    .Select(y =>
                                        new
                                        {
                                            y.Id,
                                            y.Configuration,
                                            y.ApprovedSqlHash
                                        })
                                    .ToList()
                            })
                        .FirstOrDefaultAsync(cancellationToken);
                    if (contract == null)
                    {
                        return GovernedState.Missing;
                    }

                    var awaiting = contract.Rules
                        .Select(x => new AwaitingRule(x.Id, CustomSqlRuleDefinition.Hash(contract.DataSourceId, x.Configuration), x.ApprovedSqlHash))
                        .Where(x => !ChangeApprovalHashes.Allows(x.ApprovedHash, x.Hash))
                        .ToList();

                    var setHash = CustomSqlRuleDefinition.SetHash(awaiting.Select(x => x.Hash));

                    return new GovernedState(true, contract.Name, setHash, setHash, awaiting);
                }

            case ChangeApprovalKind.RecipientDestination:
                {
                    var recipient = await context.Recipients
                        .Where(x => x.Id == resourceId)
                        .Select(x =>
                            new
                            {
                                x.Name,
                                x.NotificationType,
                                x.Destination,
                                x.HeadersJson
                            })
                        .FirstOrDefaultAsync(cancellationToken);

                    if (recipient == null)
                    {
                        return GovernedState.Missing;
                    }

                    var canonical = RecipientDestinationDefinition.CanonicalOfStored(secretProtector, recipient.NotificationType, recipient.Destination, recipient.HeadersJson);
                    if (canonical == null)
                    {
                        return new GovernedState(true, recipient.Name, null, null, []);
                    }

                    var current = hasher.Hash(canonical);

                    return new GovernedState(true, recipient.Name, hasher.Matches(requestedHash, canonical) ? requestedHash : current, current, []);
                }

            case ChangeApprovalKind.DataSourceConnection:
                {
                    // The full entity: the definition reader is the one place a stored connection becomes a definition hash.
                    var dataSource = await context.DataSources
                        .AsNoTracking()
                        .Where(x => x.Id == resourceId)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (dataSource == null)
                    {
                        return GovernedState.Missing;
                    }

                    var current = definitionReader.CurrentDefinitionHash(dataSource);
                    if (current == null)
                    {
                        return new GovernedState(true, dataSource.Name, null, null, []);
                    }

                    var holdsRequested = definitionReader.DefinitionMatches(dataSource, requestedHash);

                    return new GovernedState(true, dataSource.Name, holdsRequested ? requestedHash : current, current, []);
                }

            case ChangeApprovalKind.LlmConnection:
                {
                    // The one stored connection always exists; the request applies while it is the one the request was made from.
                    var current = await llmConnections.StateAsync(context, requestedHash, cancellationToken);

                    return new GovernedState(true, LlmConnectionApproval.ResourceName, current, current, []);
                }

            default:
                return GovernedState.Missing;
        }
    }

    // Approved: the resource works with the definition the request records. For a data contract, each rule that waited
    // gets the hash of its own SQL; for a data source, the hash of its definition under the current key; for the LLM
    // connection, the staged connection is written (its settings save, whose event and after-commit step are returned).
    private async Task<StagedAppSettings?> ApplyAsync(
        BeaconContext context,
        int requestId,
        RequestToDecide request,
        GovernedState state,
        LlmConnectionDefinition? llmProposal,
        CancellationToken cancellationToken)
    {
        switch (request.Kind)
        {
            case ChangeApprovalKind.MigrationJob:
                await context.MigrationJobs
                    .Where(x => x.Id == request.ResourceId)
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.ApprovedDefinitionHash, request.DefinitionHash), cancellationToken);
                break;
            case ChangeApprovalKind.CustomSqlRules:
                foreach (var rule in state.AwaitingRules)
                {
                    await context.DataContractRules
                        .Where(x => x.Id == rule.Id)
                        .Where(x => x.DataContractId == request.ResourceId)
                        .ExecuteUpdateAsync(x => x.SetProperty(y => y.ApprovedSqlHash, rule.Hash), cancellationToken);
                }

                break;
            case ChangeApprovalKind.RecipientDestination:
                await context.Recipients
                    .Where(x => x.Id == request.ResourceId)
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.ApprovedDestinationHash, state.ApprovedHash), cancellationToken);
                break;
            case ChangeApprovalKind.DataSourceConnection:
                await context.DataSources
                    .Where(x => x.Id == request.ResourceId)
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.ApprovedTargetHash, state.ApprovedHash), cancellationToken);
                break;
            case ChangeApprovalKind.LlmConnection when llmProposal != null:
                return await llmConnections.ApplyAsync(context, requestId, llmProposal, request.RequestedByUserId, cancellationToken);
            default:
                // Never a silent approval: what is not applied is refused, and the decision rolls back with it.
                logger.LogWarning("Change request of kind {Kind} refused: this version does not apply it", request.Kind);

                throw new BeaconConflictException(UnreadableProposalMessage);
        }

        return null;
    }

    // After the commit (the decision stands): an approved LLM connection is applied (the settings cache cleared, the LLM
    // configuration updated), false when that failed; an approved data source's catalog is read with its connection now
    // approved, best effort. A failure is logged by the request id and the exception type only.
    private async Task<bool> AfterApprovalAsync(int requestId, RequestToDecide request, StagedAppSettings? appliedSettings, CancellationToken cancellationToken)
    {
        if (appliedSettings != null)
        {
            try
            {
                await appliedSettings.AfterCommitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    "Change request {RequestId} of the LLM connection was approved and stored, but could not be applied in this process ({ExceptionType}); it applies at the next start",
                    requestId,
                    ex.GetType().Name);

                return false;
            }

            return true;
        }

        if (request.Kind != ChangeApprovalKind.DataSourceConnection)
        {
            return true;
        }

        try
        {
            await heldDataSourceProbe.ConnectionApprovedAsync(request.ResourceId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "The connection of data source {DataSourceId} was approved, but its metadata could not be refreshed ({ExceptionType}); refresh it from the data source",
                request.ResourceId,
                ex.GetType().Name);
        }

        return true;
    }

    // Why a request of a resource in this state cannot be approved: it is gone, cannot be read, or holds another
    // definition. Null when it holds exactly the definition the request proposes.
    private static string? StaleReason(ChangeApprovalKind kind, GovernedState state, string definitionHash)
    {
        if (!state.Exists)
        {
            return ResourceGoneMessage;
        }

        if (state.DefinitionHash == null)
        {
            return kind switch
            {
                ChangeApprovalKind.DataSourceConnection => UnreadableConnectionMessage,
                ChangeApprovalKind.LlmConnection => UnreadableProposalMessage,
                _ => UnreadableMessage
            };
        }

        return string.Equals(state.DefinitionHash, definitionHash, StringComparison.Ordinal) ? null : ChangedSinceSubmittedMessage;
    }

    // Four eyes, as for query changes: the requester never approves their own change, whichever way either signed in (a
    // recorded id is compared with every id the reviewer's request presents); a request without a recorded requester cannot
    // be told apart from the reviewer's own, so it is only rejected. Null when the reviewer may approve.
    private string? FourEyesRefusal(BeaconActor reviewer, string? requestedByUserId)
    {
        if (reviewer.UserId == null)
        {
            return QueryApprovalService.UnidentifiedReviewerMessage;
        }

        if (configuration.ApprovalWorkflow.AllowSelfApproval)
        {
            return null;
        }

        if (requestedByUserId == null)
        {
            return QueryApprovalService.NoRequesterMessage;
        }

        return reviewer.IsRecordedAs(requestedByUserId) ? QueryApprovalService.OwnRequestMessage : null;
    }

    // The names of the data sources the request's definitions name, archived ones included (a request outlives them).
    private static async Task<Dictionary<int, string>> DataSourceNamesAsync(
        BeaconContext context,
        ChangeApprovalKind kind,
        string proposedJson,
        string? previousJson,
        CancellationToken cancellationToken)
    {
        if (kind is not (ChangeApprovalKind.MigrationJob or ChangeApprovalKind.CustomSqlRules))
        {
            return [];
        }

        var ids = new List<int>();
        foreach (var json in new[] { proposedJson, previousJson })
        {
            if (Read<MigrationJobDefinitionView>(kind, json, ChangeApprovalKind.MigrationJob) is { } job)
            {
                ids.Add(job.SourceDataSourceId);
                ids.Add(job.DestinationDataSourceId);
            }

            if (Read<CustomSqlRulesView>(kind, json, ChangeApprovalKind.CustomSqlRules) is { } rules)
            {
                ids.Add(rules.DataSourceId);
            }
        }

        var distinct = ids
            .Distinct()
            .ToList();

        return await context.DataSources
            .IgnoreQueryFilters()
            .Where(x => distinct.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
    }

    // The projects a data source is linked to now, read live: they are not part of what a request proposes, so a link made
    // or removed since does not make the request stale. Empty for every other kind.
    private static async Task<IReadOnlyList<ChangeApprovalProject>> CurrentProjectsAsync(
        BeaconContext context,
        ChangeApprovalKind kind,
        int resourceId,
        CancellationToken cancellationToken)
    {
        if (kind != ChangeApprovalKind.DataSourceConnection)
        {
            return [];
        }

        return await context.ProjectDataSources
            .Where(x => x.DataSourceId == resourceId)
            .OrderBy(x => x.Project.Name)
            .Select(x =>
                new ChangeApprovalProject
                {
                    Id = x.ProjectId,
                    Name = x.Project.Name
                })
            .ToListAsync(cancellationToken);
    }

    // A recorded view of the request's kind; null for another kind, for none, and for one that cannot be read.
    private static T? Read<T>(ChangeApprovalKind requestKind, string? json, ChangeApprovalKind kind) where T : class
    {
        if (requestKind != kind || string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Whether what a request proposes can be read as its kind's view: one that cannot is never approved.
    private static bool ProposalReadable(ChangeApprovalKind kind, string proposedJson)
    {
        return kind switch
        {
            ChangeApprovalKind.MigrationJob => Read<MigrationJobDefinitionView>(kind, proposedJson, kind) != null,
            ChangeApprovalKind.CustomSqlRules => Read<CustomSqlRulesView>(kind, proposedJson, kind) != null,
            ChangeApprovalKind.RecipientDestination => Read<RecipientDestinationView>(kind, proposedJson, kind) != null,
            ChangeApprovalKind.DataSourceConnection => Read<DataSourceConnectionView>(kind, proposedJson, kind) != null,
            ChangeApprovalKind.LlmConnection => Read<LlmConnectionView>(kind, proposedJson, kind) != null,
            _ => false
        };
    }

    // The requests the scope reads: for an Approver every one but the LLM connection's, which only a live Admin reads; for
    // anyone else, like query approvals, those of the migration jobs whose source and destination it reads and of the data
    // contracts whose data source it reads — never a recipient's (recipients are project-less) or a connection's, nor one
    // of a resource that no longer exists.
    internal static IQueryable<ChangeApprovalRequest> Readable(BeaconContext context, ChangeApprovalScope scope)
    {
        if (scope.IsApprover)
        {
            return scope.IsLiveAdmin
                ? context.ChangeApprovalRequests
                : context.ChangeApprovalRequests.Where(x => x.Kind != ChangeApprovalKind.LlmConnection);
        }

        var jobs = context.MigrationJobs
            .WhereSourceAccessible(context, scope.Readable, x => x.DataSourceId)
            .WhereSourceAccessible(context, scope.Readable, x => x.DestinationDataSourceId)
            .Select(x => x.Id);
        var contracts = context.DataContracts
            .WhereSourceAccessible(context, scope.Readable, x => x.DataSourceId)
            .Select(x => x.Id);

        return context.ChangeApprovalRequests
            .Where(x => (x.Kind == ChangeApprovalKind.MigrationJob && jobs.Contains(x.ResourceId))
                || (x.Kind == ChangeApprovalKind.CustomSqlRules && contracts.Contains(x.ResourceId)));
    }

    private static MigrationJobDefinitionView? Named(MigrationJobDefinitionView? view, Dictionary<int, string> names)
    {
        return view == null
            ? null
            : view with
            {
                SourceDataSourceName = names.GetValueOrDefault(view.SourceDataSourceId),
                DestinationDataSourceName = names.GetValueOrDefault(view.DestinationDataSourceId)
            };
    }

    private static CustomSqlRulesView? Named(CustomSqlRulesView? view, Dictionary<int, string> names)
    {
        return view == null ? null : view with { DataSourceName = names.GetValueOrDefault(view.DataSourceId) };
    }

    // The decision is persisted; the push tells open browser tabs. A failed push is logged, never a failed decision.
    private async Task NotifyAsync(int requestId, string status, string? reviewerUserId, string? requesterUserId, CancellationToken cancellationToken)
    {
        if (approvalNotifier == null)
        {
            logger.LogWarning(
                "Change request {RequestId} was {Status} but no IApprovalNotifier is registered, so no client was notified. Call AddBeaconApiServices() to enable push.",
                requestId,
                status);

            return;
        }

        try
        {
            await approvalNotifier.ApprovalUpdatedAsync(requestId, status, reviewerUserId, requesterUserId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Change request {RequestId} was {Status}, but its push failed ({ExceptionType})", requestId, status, ex.GetType().Name);
        }
    }

    private sealed class RequestDetail
    {
        public int Id { get; init; }

        public ChangeApprovalKind Kind { get; init; }

        public int ResourceId { get; init; }

        public string ResourceName { get; init; } = string.Empty;

        public ApprovalStatus Status { get; init; }

        public string? RequestedByUserId { get; init; }

        public string? RequestedByUserName { get; init; }

        public string? ReviewedByUserId { get; init; }

        public string? ReviewedByUserName { get; init; }

        public DateTime? ReviewedAt { get; init; }

        public string? ReviewComment { get; init; }

        public string? ChangeSummary { get; init; }

        public DateTime CreatedTime { get; init; }

        public string DefinitionHash { get; init; } = string.Empty;

        public string PayloadHash { get; init; } = string.Empty;

        public string ProposedJson { get; init; } = string.Empty;

        public string? PreviousJson { get; init; }
    }

    private sealed class RequestToDecide
    {
        public ChangeApprovalKind Kind { get; init; }

        public int ResourceId { get; init; }

        public ApprovalStatus Status { get; init; }

        public string? RequestedByUserId { get; init; }

        public string DefinitionHash { get; init; } = string.Empty;

        public string PayloadHash { get; init; } = string.Empty;

        public string ProposedJson { get; init; } = string.Empty;

        public string? PreviousJson { get; init; }
    }

    private sealed record AwaitingRule(int Id, string Hash, string? ApprovedHash);

    // ApprovedHash: what an approval records on the resource (for a recipient, its definition under the current key).
    private sealed record GovernedState(bool Exists, string? Name, string? DefinitionHash, string? ApprovedHash, IReadOnlyList<AwaitingRule> AwaitingRules)
    {
        public static GovernedState Missing { get; } = new(false, null, null, null, []);
    }
}

/// <summary>
/// Who reads change requests: an Approver (an Admin holds the permission) reads every one but the LLM connection's, which
/// only a live Admin (<see cref="IsLiveAdmin"/>) reads; anyone else the requests of the migration jobs and data contracts
/// whose data sources <see cref="Readable"/> reaches.
/// </summary>
public sealed record ChangeApprovalScope(ProjectSet Readable, bool IsApprover)
{
    /// <summary>An Approver's: every change request but the LLM connection's.</summary>
    public static ChangeApprovalScope Approver { get; } = new(ProjectSet.All, true);

    /// <summary>
    /// The caller is an Admin by their live permissions, so they also read the LLM connection's change requests. False by
    /// default: a scope built without it reads none of them.
    /// </summary>
    public bool IsLiveAdmin { get; init; }
}
