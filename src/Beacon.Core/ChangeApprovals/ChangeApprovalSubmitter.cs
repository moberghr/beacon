using System.Text.Json;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// Submits a change of a governed resource (a migration job, a data contract's Custom SQL rules, a recipient, a data
/// source's connection) while the workflow covers its kind (<see cref="ApprovalWorkflowOptions.Covers"/>). The requester
/// is the caller (<see cref="IBeaconActorAccessor"/>), resolved before anything is written; a caller Beacon cannot name
/// submits nothing (403) unless the host allows self-approval. Inside the unit of work that writes the resource — its row
/// taken first, so submissions and decisions on one resource run one after the other — a change that alters the
/// definition is refused (409) while someone else's change of the resource waits, and so is any submission that would
/// supersede someone else's pending request; the requester's own earlier pending request is superseded (and told after
/// the commit); a definition that is not the approved one gets a request unless a pending one already proposes exactly it
/// (for a recipient or a data source's connection, under the current or a previous key).
/// </summary>
internal sealed class ChangeApprovalSubmitter(
    IBeaconActorAccessor actorAccessor,
    IBeaconUserContext userContext,
    BeaconConfiguration configuration,
    ILogger<ChangeApprovalSubmitter> logger,
    IApprovalNotifier? approvalNotifier = null)
{
    /// <summary>Why a caller Beacon cannot name submits no change that waits for approval.</summary>
    internal const string UnidentifiedRequesterMessage =
        "A change that waits for approval needs a requester Beacon can name: sign in as a Beacon user to make it.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>True when the workflow is on and covers changes of <paramref name="kind"/>.</summary>
    public bool Covers(ChangeApprovalKind kind)
    {
        return configuration.ApprovalWorkflow.Covers(kind);
    }

    /// <summary>
    /// The caller as the requester of a change of <paramref name="kind"/>, or null when the workflow does not cover the
    /// kind. Refuses (403) a caller without a user id unless the host allows self-approval: nobody could approve their
    /// change. Called before anything is written.
    /// </summary>
    public async Task<ChangeRequester?> RequesterAsync(ChangeApprovalKind kind, int? resourceId, CancellationToken cancellationToken)
    {
        if (!Covers(kind))
        {
            return null;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (actor.UserId == null && !configuration.ApprovalWorkflow.AllowSelfApproval)
        {
            throw AccessRefusal.Of(logger, actor, ResourceOf(kind), resourceId, UnidentifiedRequesterMessage);
        }

        return new ChangeRequester(actor.UserId, userContext.DisplayName);
    }

    /// <summary>
    /// The caller as the requester of a change of <paramref name="kind"/> that may apply at once, or null when the workflow
    /// does not cover the kind. Refuses nobody: <see cref="DemandNamedAsync"/> refuses a caller without a user id once the
    /// change turns out to wait for approval.
    /// </summary>
    public async Task<ChangeRequester?> CallerAsync(ChangeApprovalKind kind, CancellationToken cancellationToken)
    {
        if (!Covers(kind))
        {
            return null;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        return new ChangeRequester(actor.UserId, userContext.DisplayName);
    }

    /// <summary>
    /// A change by <paramref name="requester"/> waits for approval: refused (403) when Beacon cannot name the requester and
    /// the host does not allow self-approval, as <see cref="RequesterAsync"/> refuses, before anything is written.
    /// </summary>
    public async Task DemandNamedAsync(ChangeRequester requester, ChangeApprovalKind kind, int? resourceId, CancellationToken cancellationToken)
    {
        if (requester.UserId != null || configuration.ApprovalWorkflow.AllowSelfApproval)
        {
            return;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        throw AccessRefusal.Of(logger, actor, ResourceOf(kind), resourceId, UnidentifiedRequesterMessage);
    }

    /// <summary>
    /// Records <paramref name="proposal"/> in the unit of work that writes the resource, after its row was taken (or, for a
    /// new resource, after its first save gave it an id). Adds the request (unsaved) when the definition the resource now
    /// holds needs approval and no pending request proposes exactly it; supersedes the pending requests it makes moot.
    /// </summary>
    public async Task<ChangeSubmission> SubmitAsync(
        BeaconContext context,
        ChangeRequester requester,
        ChangeProposal proposal,
        CancellationToken cancellationToken)
    {
        var pending = await context.ChangeApprovalRequests
            .Where(x => x.Kind == proposal.Kind)
            .Where(x => x.ResourceId == proposal.ResourceId)
            .Where(x => x.Status == ApprovalStatus.Pending)
            .Select(x =>
                new PendingChange(
                    x.Id,
                    x.RequestedByUserId,
                    x.DefinitionHash))
            .ToListAsync(cancellationToken);

        // A change of the definition while someone else's waits would leave theirs proposing a definition the resource no
        // longer holds: refused, so nobody's change is withdrawn by another person's save.
        var definitionChanged = !string.Equals(proposal.PreviousDefinitionHash, proposal.DefinitionHash, StringComparison.Ordinal);
        if (definitionChanged && pending.Any(x => !IsSameRequester(x.RequestedByUserId, requester.UserId)))
        {
            logger.LogWarning(
                "Change of {Kind} {ResourceId} refused: another person's change of it waits for approval",
                proposal.Kind,
                proposal.ResourceId);

            throw new BeaconConflictException(OtherPendingMessage(proposal.Kind));
        }

        // Every pending request that does not propose the definition the resource now holds can no longer be approved. A
        // submission supersedes only the requester's own: one that would supersede someone else's is refused, and theirs
        // stays as it is.
        var superseded = pending
            .Where(x => !proposal.NeedsApproval || !proposal.Proposes(x.DefinitionHash))
            .Select(x => new SupersededChange(x.Id, x.RequestedByUserId))
            .ToList();
        if (superseded.Any(x => !IsSameRequester(x.RequestedByUserId, requester.UserId)))
        {
            logger.LogWarning(
                "Change of {Kind} {ResourceId} refused: it would supersede another person's change of it that waits for approval",
                proposal.Kind,
                proposal.ResourceId);

            throw new BeaconConflictException(OtherPendingMessage(proposal.Kind));
        }

        if (superseded.Count > 0)
        {
            var supersededIds = superseded
                .Select(x => x.Id)
                .ToList();
            await SupersedeAsync(context, proposal.Kind, supersededIds, cancellationToken);
        }

        var alreadyProposed = pending.Any(x => proposal.Proposes(x.DefinitionHash));
        if (!proposal.NeedsApproval || alreadyProposed)
        {
            return new ChangeSubmission(superseded, null);
        }

        var proposedJson = JsonSerializer.Serialize(proposal.Proposed, proposal.Proposed.GetType(), JsonOptions);
        var request = new ChangeApprovalRequest
        {
            Kind = proposal.Kind,
            ResourceId = proposal.ResourceId,
            ResourceName = proposal.ResourceName,
            Status = ApprovalStatus.Pending,
            DefinitionHash = proposal.DefinitionHash,
            PayloadHash = ChangeApprovalHashes.Sha256(proposedJson),
            ProposedJson = proposedJson,
            PreviousJson = proposal.Previous == null ? null : JsonSerializer.Serialize(proposal.Previous, proposal.Previous.GetType(), JsonOptions),
            RequestedByUserId = requester.UserId,
            RequestedByUserName = await ChangeAuthorLabel.OfAsync(context, requester.UserId, requester.DisplayName, cancellationToken),
            ChangeSummary = proposal.ChangeSummary,
            EncryptedPayload = proposal.EncryptedPayload
        };

        context.ChangeApprovalRequests.Add(request);

        return new ChangeSubmission(superseded, request);
    }

    /// <summary>
    /// A save made while the workflow does not cover <paramref name="kind"/> that leaves a governed resource holding
    /// <paramref name="definitionHash"/> (null: the resource is no longer governed): every pending request of it that does
    /// not propose exactly that definition can no longer be approved, so it is superseded (whoever requested it), and its
    /// requester is told after the commit.
    /// </summary>
    public async Task<ChangeSubmission> SupersedeOrphanedAsync(
        BeaconContext context,
        ChangeApprovalKind kind,
        int resourceId,
        string? definitionHash,
        CancellationToken cancellationToken)
    {
        var orphaned = await context.ChangeApprovalRequests
            .Where(x => x.Kind == kind)
            .Where(x => x.ResourceId == resourceId)
            .Where(x => x.Status == ApprovalStatus.Pending)
            .WhereIf(definitionHash != null, x => x.DefinitionHash != definitionHash)
            .Select(x => new SupersededChange(x.Id, x.RequestedByUserId))
            .ToListAsync(cancellationToken);
        if (orphaned.Count == 0)
        {
            return new ChangeSubmission([], null);
        }

        var ids = orphaned
            .Select(x => x.Id)
            .ToList();
        await SupersedeAsync(context, kind, ids, cancellationToken);
        logger.LogInformation(
            "{RequestCount} pending change requests of {Kind} {ResourceId} were superseded: a save made while the approval workflow does not review it changed what they propose",
            orphaned.Count,
            kind,
            resourceId);

        return new ChangeSubmission(orphaned, null);
    }

    /// <summary>
    /// The resource of <paramref name="kind"/> is deleted in this unit of work: every pending request of it can no longer
    /// be approved, so it is superseded (whoever requested it), and its requester is told after the commit.
    /// </summary>
    public async Task<ChangeSubmission> SupersedeOfDeletedAsync(
        BeaconContext context,
        ChangeApprovalKind kind,
        int resourceId,
        CancellationToken cancellationToken)
    {
        var pending = await context.ChangeApprovalRequests
            .Where(x => x.Kind == kind)
            .Where(x => x.ResourceId == resourceId)
            .Where(x => x.Status == ApprovalStatus.Pending)
            .Select(x => new SupersededChange(x.Id, x.RequestedByUserId))
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return new ChangeSubmission([], null);
        }

        var ids = pending
            .Select(x => x.Id)
            .ToList();
        await SupersedeAsync(context, kind, ids, cancellationToken);
        logger.LogInformation(
            "{RequestCount} pending change requests of {Kind} {ResourceId} were superseded: the resource was deleted",
            pending.Count,
            kind,
            resourceId);

        return new ChangeSubmission(pending, null);
    }

    /// <summary>
    /// Tells each superseded request's requester, after the unit of work committed; a failed push is logged, never a failed
    /// change.
    /// </summary>
    public async Task NotifySupersededAsync(ChangeSubmission? submission, CancellationToken cancellationToken)
    {
        if (submission == null || submission.Superseded.Count == 0)
        {
            return;
        }

        if (approvalNotifier == null)
        {
            logger.LogWarning(
                "{RequestCount} change requests were superseded but no IApprovalNotifier is registered, so no requester was notified. Call AddBeaconApiServices() to enable push.",
                submission.Superseded.Count);

            return;
        }

        foreach (var request in submission.Superseded)
        {
            try
            {
                await approvalNotifier.ApprovalUpdatedAsync(request.Id, QueryVersionService.SupersededStatus, null, request.RequestedByUserId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("The requester of superseded change request {RequestId} could not be notified ({ExceptionType})", request.Id, ex.GetType().Name);
            }
        }
    }

    /// <summary>How a resource of <paramref name="kind"/> is named in refusals and logs.</summary>
    internal static string ResourceOf(ChangeApprovalKind kind)
    {
        return kind switch
        {
            ChangeApprovalKind.MigrationJob => "migration job",
            ChangeApprovalKind.CustomSqlRules => "data contract",
            ChangeApprovalKind.RecipientDestination => "recipient",
            ChangeApprovalKind.DataSourceConnection => "data source",
            ChangeApprovalKind.LlmConnection => "LLM connection",
            _ => "resource"
        };
    }

    /// <summary>Why a change of the definition is refused while someone else's change of the resource waits.</summary>
    internal static string OtherPendingMessage(ChangeApprovalKind kind)
    {
        return $"Another person's change of this {ResourceOf(kind)} waits for approval: it is approved, rejected or withdrawn first.";
    }

    // Marks the pending requests among supersededIds Superseded. A superseded LLM-connection request can never be applied,
    // so the encrypted connection it carries is wiped in the same update; no other kind carries one.
    private static Task<int> SupersedeAsync(BeaconContext context, ChangeApprovalKind kind, List<int> supersededIds, CancellationToken cancellationToken)
    {
        var pending = context.ChangeApprovalRequests
            .Where(x => supersededIds.Contains(x.Id))
            .Where(x => x.Status == ApprovalStatus.Pending);

        return kind == ChangeApprovalKind.LlmConnection
            ? pending.ExecuteUpdateAsync(
                x => x
                    .SetProperty(y => y.Status, ApprovalStatus.Superseded)
                    .SetProperty(y => y.EncryptedPayload, (string?)null),
                cancellationToken)
            : pending.ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, ApprovalStatus.Superseded), cancellationToken);
    }

    // The same person by the id a request recorded for them (ignoring case, as identity providers may change it).
    private static bool IsSameRequester(string? recorded, string? requester)
    {
        return recorded != null && requester != null && string.Equals(recorded, requester, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record PendingChange(int Id, string? RequestedByUserId, string DefinitionHash);
}

/// <summary>Who submits a governed change (<see cref="ChangeApprovalSubmitter.RequesterAsync"/>), resolved before anything is written.</summary>
internal sealed record ChangeRequester(string? UserId, string? DisplayName);

/// <summary>
/// A change of a governed resource as it is submitted: the definition hash it held before (null for a resource the change
/// creates) and the one it holds after, whether that one needs approval (it is not the approved definition), and what a
/// reviewer is shown of the resource before and after. <c>MatchesDefinition</c> tells whether a pending
/// request's hash names the definition the resource holds after, where a hash is not compared as written: a recipient's
/// keyed hash also matches when it was written under a previous key. <c>EncryptedPayload</c> is what the request stores,
/// encrypted, for its approval to apply (the LLM connection's staged values).
/// </summary>
internal sealed record ChangeProposal(
    ChangeApprovalKind Kind,
    int ResourceId,
    string ResourceName,
    string? PreviousDefinitionHash,
    string DefinitionHash,
    bool NeedsApproval,
    object Proposed,
    object? Previous,
    string ChangeSummary,
    Func<string, bool>? MatchesDefinition = null,
    string? EncryptedPayload = null)
{
    /// <summary>
    /// True when a pending request whose definition hash is <paramref name="definitionHash"/> proposes the definition the
    /// resource holds after the change.
    /// </summary>
    public bool Proposes(string definitionHash)
    {
        return MatchesDefinition?.Invoke(definitionHash) ?? string.Equals(definitionHash, DefinitionHash, StringComparison.Ordinal);
    }
}

/// <summary>What a submission did: the pending requests it superseded (told after the commit), and the request it added.</summary>
internal sealed record ChangeSubmission(IReadOnlyList<SupersededChange> Superseded, ChangeApprovalRequest? Request);

internal sealed record SupersededChange(int Id, string? RequestedByUserId);
