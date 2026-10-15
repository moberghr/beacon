using System.Text.Json;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// Change requests of migration jobs, data contracts' Custom SQL rules and recipients: the pending ones, one with what it
/// changes, and the decisions on them. The same rules as query approvals: the reviewer is the caller
/// (<see cref="IBeaconActorAccessor"/>) and never the requester (unless <see cref="ApprovalWorkflowOptions.AllowSelfApproval"/>;
/// a request without a recorded requester is only rejected); a request is decided once, from Pending, by a conditional
/// update in a transaction that takes the resource's row first; an approval names the payload the reviewer was shown and
/// applies only while the resource still holds exactly the definition the request records. An Approver reads every
/// request; anyone else reads, like query approvals, the requests of the migration jobs and data contracts it reads
/// (<see cref="ChangeApprovalScope"/>), never a recipient's.
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
    /// Approves a pending request as the caller: 403 for its requester (by any id the caller presents) or a caller without
    /// a user id, and for a request without a recorded requester; 409 when it is no longer pending, when
    /// <paramref name="expectedPayloadHash"/> is not the payload the detail shows, when what it proposes cannot be read,
    /// when the resource no longer exists or no longer holds exactly the definition the request records; 400 without a
    /// comment when the change lies only in a masked part (<see cref="ChangeApprovalDetail.CommentRequiredToApprove"/>).
    /// The resource then works with that definition.
    /// </summary>
    Task<ChangeApprovalDecision> ApproveAsync(int requestId, string? comment, string? expectedPayloadHash, CancellationToken cancellationToken);

    /// <summary>
    /// Rejects a pending request as the caller (its requester may withdraw it); 409 when it is no longer pending. The
    /// resource keeps the definition it holds, which stays inactive until it is changed back or changed and approved.
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<PagedList<ChangeApprovalSummary>> GetPendingAsync(ListRequest request, ChangeApprovalKind? kind, ChangeApprovalScope scope, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

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

        var request = await Readable(context, scope)
            .AsNoTracking()
            .Where(x => x.Id == requestId)
            .FirstOrDefaultAsync(cancellationToken);
        if (request == null)
        {
            return null;
        }

        var state = await StateAsync(context, request.Kind, request.ResourceId, request.DefinitionHash, cancellationToken);
        var caller = await actorAccessor.GetCurrentAsync(cancellationToken);
        var isPending = request.Status == ApprovalStatus.Pending;
        var staleReason = StaleReason(state, request.DefinitionHash);
        var unreadable = !ProposalReadable(request.Kind, request.ProposedJson);
        var blocked = isPending
            ? FourEyesRefusal(caller, request.RequestedByUserId) ?? (unreadable ? UnreadableProposalMessage : null) ?? staleReason
            : null;
        var names = await DataSourceNamesAsync(context, request, cancellationToken);
        var previousDestination = Read<RecipientDestinationView>(request.Kind, request.PreviousJson, ChangeApprovalKind.RecipientDestination);
        var proposedDestination = Read<RecipientDestinationView>(request.Kind, request.ProposedJson, ChangeApprovalKind.RecipientDestination);
        var hiddenByMask = RecipientDestinationDefinition.ChangeHiddenByMask(previousDestination, proposedDestination);

        return new ChangeApprovalDetail
        {
            Id = request.Id,
            Kind = request.Kind,
            ResourceId = request.ResourceId,
            ResourceName = state.Name ?? request.ResourceName,
            ResourceExists = state.Exists,
            Status = request.Status,
            RequestedByUserName = request.RequestedByUserName,
            ReviewedByUserName = request.ReviewedByUserName,
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
            PayloadHash = request.PayloadHash,
            IsOwnRequest = caller.IsRecordedAs(request.RequestedByUserId),
            MayApprove = isPending && blocked == null,
            ApproveBlockedReason = blocked,
            IsStale = isPending && staleReason != null,
            ChangeHiddenByMask = hiddenByMask,
            CommentRequiredToApprove = isPending && hiddenByMask
        };
    }

    public async Task<ChangeApprovalDecision> ApproveAsync(int requestId, string? comment, string? expectedPayloadHash, CancellationToken cancellationToken)
    {
        var reviewer = await actorAccessor.GetCurrentAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var request = await RequestToDecideAsync(context, requestId, cancellationToken);

        EnsurePending(request.Status);
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

        var reviewerName = await ChangeAuthorLabel.OfAsync(context, reviewer.UserId, userContext.DisplayName, cancellationToken);
        var reviewedAt = DateTime.UtcNow;

        // One unit of work: the resource's row first (as a submission takes it), the definition it holds checked under that
        // lock, the decision from Pending and the approved definition, committed together or not at all. Through the
        // execution strategy, so a host that enables retry-on-failure gets a retried unit; a retry starts from a clean
        // tracker.
        var strategy = context.Database.CreateExecutionStrategy();
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
            var staleReason = StaleReason(state, request.DefinitionHash);
            if (staleReason != null)
            {
                logger.LogWarning(
                    "Change request {RequestId} refused: {Kind} {ResourceId} no longer holds the definition it proposes",
                    requestId,
                    request.Kind,
                    request.ResourceId);

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

            await ApplyAsync(context, request, state, cancellationToken);
            await securityAudit.SaveWithEventsAsync(
                context,
                () => [ChangeApprovalAuditEntries.Decided(requestId, request.Kind, request.ResourceId, ApprovalStatus.Approved, request.RequestedByUserId)],
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        await NotifyAsync(requestId, "approved", reviewer.UserId, request.RequestedByUserId, cancellationToken);

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

        var reviewerName = await ChangeAuthorLabel.OfAsync(context, reviewer.UserId, userContext.DisplayName, cancellationToken);
        var reviewedAt = DateTime.UtcNow;

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            // The resource's row first, as submitting and approving take it; a resource that is gone takes nothing.
            await LockResourceAsync(context, request.Kind, request.ResourceId, cancellationToken);

            // Decided only from Pending: rejecting never overwrites an approval, a rejection or a superseded request.
            var decided = await context.ChangeApprovalRequests
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

            default:
                return GovernedState.Missing;
        }
    }

    // Approved: the resource works with the definition the request records. For a data contract, each rule that waited
    // gets the hash of its own SQL.
    private static async Task ApplyAsync(BeaconContext context, RequestToDecide request, GovernedState state, CancellationToken cancellationToken)
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
        }
    }

    // Why a request of a resource in this state cannot be approved: it is gone, cannot be read, or holds another
    // definition. Null when it holds exactly the definition the request proposes.
    private static string? StaleReason(GovernedState state, string definitionHash)
    {
        if (!state.Exists)
        {
            return ResourceGoneMessage;
        }

        if (state.DefinitionHash == null)
        {
            return UnreadableMessage;
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
    private static async Task<Dictionary<int, string>> DataSourceNamesAsync(BeaconContext context, ChangeApprovalRequest request, CancellationToken cancellationToken)
    {
        if (request.Kind == ChangeApprovalKind.RecipientDestination)
        {
            return [];
        }

        var ids = new List<int>();
        foreach (var json in new[] { request.ProposedJson, request.PreviousJson })
        {
            if (Read<MigrationJobDefinitionView>(request.Kind, json, ChangeApprovalKind.MigrationJob) is { } job)
            {
                ids.Add(job.SourceDataSourceId);
                ids.Add(job.DestinationDataSourceId);
            }

            if (Read<CustomSqlRulesView>(request.Kind, json, ChangeApprovalKind.CustomSqlRules) is { } rules)
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
            _ => false
        };
    }

    // The requests the scope reads: every one for an Approver; for anyone else, like query approvals, those of the migration
    // jobs whose source and destination it reads and of the data contracts whose data source it reads — never a recipient's
    // (recipients are project-less) nor one of a resource that no longer exists.
    private static IQueryable<ChangeApprovalRequest> Readable(BeaconContext context, ChangeApprovalScope scope)
    {
        if (scope.IsApprover)
        {
            return context.ChangeApprovalRequests;
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
/// Who reads change requests: an Approver (an Admin holds the permission) reads every one; anyone else the requests of
/// the migration jobs and data contracts whose data sources <see cref="Readable"/> reaches.
/// </summary>
public sealed record ChangeApprovalScope(ProjectSet Readable, bool IsApprover)
{
    /// <summary>An Approver's: every change request.</summary>
    public static ChangeApprovalScope Approver { get; } = new(ProjectSet.All, true);
}
