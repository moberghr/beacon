using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// Where a recipient delivers, under the change approval workflow (<see cref="ApprovalWorkflowOptions.CoversRecipientDestinations"/>):
/// a recipient created, or whose type, destination or headers change, receives nothing until a second person approves
/// exactly where it now delivers. A save that leaves where it delivers as it was submits nothing, and one that puts back
/// the approved destination needs no approval. With the workflow off, a governed recipient whose delivery changes is no
/// longer governed (it delivers as before the workflow), and the pending requests of it are superseded. Hashes are keyed
/// (<see cref="RecipientDestinationHasher"/>); an approved hash written under a previous key is rewritten under the current
/// key when the definition it approves is saved again.
/// </summary>
internal sealed class RecipientChangeApproval(ChangeApprovalSubmitter submitter, RecipientSecretProtector protector, RecipientDestinationHasher hasher)
{
    private const ChangeApprovalKind Kind = ChangeApprovalKind.RecipientDestination;

    /// <summary>The requester (null when the workflow does not cover recipients), resolved before anything is written.</summary>
    public Task<ChangeRequester?> RequesterAsync(int? recipientId, CancellationToken cancellationToken)
    {
        return submitter.RequesterAsync(Kind, recipientId, cancellationToken);
    }

    /// <summary>The recipient's row, taken first in a unit of work that changes it (a no-op update; 0 when it is gone).</summary>
    public static Task<int> LockAsync(BeaconContext context, int recipientId, CancellationToken cancellationToken)
    {
        return context.Recipients
            .Where(x => x.Id == recipientId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.ApprovedDestinationHash, y => y.ApprovedDestinationHash), cancellationToken);
    }

    /// <summary>The approved hash a new recipient starts with: nothing approved under the workflow, not governed otherwise.</summary>
    public static string? ApprovedHashOfNew(ChangeRequester? requester)
    {
        return requester == null ? null : ChangeApprovalHashes.NothingApproved;
    }

    /// <summary>A new recipient's request, after its first save gave it an id; none when the workflow does not cover recipients.</summary>
    public async Task<ChangeSubmission?> SubmitNewAsync(BeaconContext context, ChangeRequester? requester, Recipient recipient, CancellationToken cancellationToken)
    {
        if (requester == null)
        {
            return null;
        }

        var proposal = new ChangeProposal(
            Kind,
            recipient.Id,
            recipient.Name,
            PreviousDefinitionHash: null,
            hasher.Hash(CanonicalOf(recipient.NotificationType, recipient.Destination, recipient.HeadersJson)),
            NeedsApproval: true,
            ViewOf(recipient.NotificationType, recipient.Destination, recipient.HeadersJson),
            Previous: null,
            "Recipient created");

        return await submitter.SubmitAsync(context, requester, proposal, cancellationToken);
    }

    /// <summary>
    /// A recipient that now holds the submitted values (<paramref name="recipient"/>, tracked) and held
    /// <paramref name="before"/>: under the workflow its approved hash keeps the definition approved before (for a recipient
    /// not governed yet, the one it held), and a definition that is not the approved one is submitted. Without the workflow,
    /// a governed recipient whose delivery changed stops being governed, and every pending request of it is superseded.
    /// </summary>
    public async Task<ChangeSubmission?> SubmitUpdateAsync(
        BeaconContext context,
        ChangeRequester? requester,
        Recipient recipient,
        StoredRecipientSecrets before,
        CancellationToken cancellationToken)
    {
        var approved = recipient.ApprovedDestinationHash;
        if (requester == null && approved == null)
        {
            return null;
        }

        var previousCanonical = RecipientDestinationDefinition.CanonicalOfStored(protector, before.Type, before.Destination, before.HeadersJson);
        var canonical = CanonicalOf(recipient.NotificationType, recipient.Destination, recipient.HeadersJson);
        var unchanged = string.Equals(previousCanonical, canonical, StringComparison.Ordinal);
        if (requester == null)
        {
            if (unchanged)
            {
                return null;
            }

            // Nobody reviews it any more: what it waited for can no longer be approved.
            recipient.ApprovedDestinationHash = null;

            return await submitter.SupersedeOrphanedAsync(context, Kind, recipient.Id, null, cancellationToken);
        }

        if (approved == null && unchanged)
        {
            return null;
        }

        var hash = hasher.Hash(canonical);
        var previousHash = previousCanonical == null ? null : hasher.Hash(previousCanonical);

        // A recipient not governed yet keeps what it delivered to as approved (it delivered there before the workflow); an
        // approved definition it holds, before or after, is recorded under the current key.
        var approvedAfter = approved switch
        {
            null => previousHash ?? ChangeApprovalHashes.NothingApproved,
            _ when hasher.Matches(approved, canonical) => hash,
            _ when previousCanonical != null && hasher.Matches(approved, previousCanonical) => previousHash!,
            _ => approved
        };
        recipient.ApprovedDestinationHash = approvedAfter;

        var proposed = ViewOf(recipient.NotificationType, recipient.Destination, recipient.HeadersJson);
        var previous = RecipientDestinationDefinition.ViewOfStored(protector, hasher, before.Type, before.Destination, before.HeadersJson);
        var proposal = new ChangeProposal(
            Kind,
            recipient.Id,
            recipient.Name,
            previousHash,
            hash,
            NeedsApproval: !string.Equals(hash, approvedAfter, StringComparison.Ordinal),
            proposed,
            previous,
            SummaryOf(previous, proposed));

        return await submitter.SubmitAsync(context, requester, proposal, cancellationToken);
    }

    /// <summary>Tells the requesters of the requests a submission superseded, after its unit of work committed.</summary>
    public Task NotifySupersededAsync(ChangeSubmission? submission, CancellationToken cancellationToken)
    {
        return submitter.NotifySupersededAsync(submission, cancellationToken);
    }

    // The values were just prepared and stored by this unit of work, so they read back.
    private string CanonicalOf(NotificationType type, string storedDestination, string? storedHeadersJson)
    {
        return RecipientDestinationDefinition.CanonicalOfStored(protector, type, storedDestination, storedHeadersJson)
            ?? throw new InvalidOperationException("The recipient's destination could not be read back.");
    }

    private RecipientDestinationView ViewOf(NotificationType type, string storedDestination, string? storedHeadersJson)
    {
        return RecipientDestinationDefinition.ViewOfStored(protector, hasher, type, storedDestination, storedHeadersJson);
    }

    // What changed, in the masked terms a reviewer sees; a change only a secret part carries is said, as the masks match.
    private static string SummaryOf(RecipientDestinationView previous, RecipientDestinationView proposed)
    {
        if (previous.NotificationType != proposed.NotificationType)
        {
            return "Delivery type and destination changed";
        }

        if (!string.Equals(previous.Destination, proposed.Destination, StringComparison.Ordinal))
        {
            return "Destination changed";
        }

        if (!previous.HeaderNames.SequenceEqual(proposed.HeaderNames, StringComparer.Ordinal))
        {
            return "Custom headers changed";
        }

        return "Destination or header values changed (the change is in a part the mask hides)";
    }
}
