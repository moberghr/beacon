using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// Which recipients receive nothing while the change approval workflow covers recipients: those it governs whose
/// destination (type, destination and headers, decrypted) is not the approved one, or cannot be read. A recipient the
/// workflow does not govern, and every recipient while it is off, receives deliveries as before. A delivery is checked
/// on the very copy it sends, against the approved hash read at that moment (archived recipients included): a copy whose
/// recipient is gone is held, so neither a change nor an archive between loading and sending lets an unapproved copy out.
/// </summary>
internal sealed class RecipientDeliveryApproval(
    IDbContextFactory<BeaconContext> contextFactory,
    RecipientSecretProtector protector,
    RecipientDestinationHasher hasher,
    BeaconConfiguration configuration)
{
    /// <summary>True while the workflow covers recipients.</summary>
    public bool Covers => configuration.ApprovalWorkflow.Covers(ChangeApprovalKind.RecipientDestination);

    /// <summary>
    /// The recipients among <paramref name="recipientIds"/> whose stored destination receives nothing now (empty while the
    /// workflow does not cover recipients); for showing which recipients wait.
    /// </summary>
    public async Task<IReadOnlySet<int>> HeldAsync(IReadOnlyCollection<int> recipientIds, CancellationToken cancellationToken)
    {
        if (recipientIds.Count == 0 || !Covers)
        {
            return new HashSet<int>();
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var governed = await context.Recipients
            .Where(x => recipientIds.Contains(x.Id))
            .Where(x => x.ApprovedDestinationHash != null)
            .Select(x =>
                new
                {
                    x.Id,
                    x.NotificationType,
                    x.Destination,
                    x.HeadersJson,
                    x.ApprovedDestinationHash
                })
            .ToListAsync(cancellationToken);

        return governed
            .Where(x => !RecipientDestinationDefinition.Allows(
                hasher,
                x.ApprovedDestinationHash,
                RecipientDestinationDefinition.CanonicalOfStored(protector, x.NotificationType, x.Destination, x.HeadersJson)))
            .Select(x => x.Id)
            .ToHashSet();
    }

    /// <summary>
    /// The recipients among <paramref name="recipientIds"/> whose stored destination receives nothing now, by id, each with
    /// the pending request of its change, if one waits, or else the last decision on it (a rejection says to change it and
    /// save again). Empty while the workflow does not cover recipients.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, AwaitingApproval>> AwaitingAsync(IReadOnlyCollection<int> recipientIds, CancellationToken cancellationToken)
    {
        var held = (await HeldAsync(recipientIds, cancellationToken)).ToList();
        if (held.Count == 0)
        {
            return new Dictionary<int, AwaitingApproval>();
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var pending = await context.ChangeApprovalRequests
            .Where(x => x.Kind == ChangeApprovalKind.RecipientDestination)
            .Where(x => x.Status == ApprovalStatus.Pending)
            .Where(x => held.Contains(x.ResourceId))
            .Select(x =>
                new
                {
                    x.ResourceId,
                    x.Id
                })
            .ToListAsync(cancellationToken);
        var pendingByRecipient = pending
            .GroupBy(x => x.ResourceId)
            .ToDictionary(x => x.Key, x => x.Max(y => y.Id));
        var decided = await ChangeApprovalOutcomes.LastDecidedAsync(
            context,
            ChangeApprovalKind.RecipientDestination,
            held
                .Where(x => !pendingByRecipient.ContainsKey(x))
                .ToList(),
            cancellationToken);

        return held.ToDictionary(
            x => x,
            x => new AwaitingApproval(pendingByRecipient.TryGetValue(x, out var requestId) ? requestId : null, decided.GetValueOrDefault(x)));
    }

    /// <summary>
    /// The copies among <paramref name="copies"/> (what a delivery loaded and will send) that receive nothing, by recipient
    /// id: each is checked against its recipient's approved hash as it is now, archived recipients included; a copy whose
    /// recipient no longer exists is held. Empty while the workflow does not cover recipients.
    /// </summary>
    public async Task<IReadOnlySet<int>> HeldCopiesAsync(IReadOnlyCollection<RecipientCopy> copies, CancellationToken cancellationToken)
    {
        if (copies.Count == 0 || !Covers)
        {
            return new HashSet<int>();
        }

        var ids = copies
            .Select(x => x.RecipientId)
            .Distinct()
            .ToList();
        var approved = await ApprovedHashesAsync(ids, cancellationToken);

        return copies
            .Where(x => !approved.TryGetValue(x.RecipientId, out var hash)
                || !RecipientDestinationDefinition.Allows(
                    hasher,
                    hash,
                    RecipientDestinationDefinition.CanonicalOfStored(protector, x.Type, x.StoredDestination, x.StoredHeadersJson)))
            .Select(x => x.RecipientId)
            .ToHashSet();
    }

    /// <summary>
    /// True when recipient <paramref name="recipientId"/> may receive a delivery to the plaintext
    /// <paramref name="destination"/> and <paramref name="headersJson"/> of <paramref name="type"/>, as its approved hash
    /// is now (archived included): always while the workflow does not cover recipients, never when the recipient no longer
    /// exists.
    /// </summary>
    public async Task<bool> AllowsAsync(int recipientId, NotificationType type, string destination, string? headersJson, CancellationToken cancellationToken)
    {
        if (!Covers)
        {
            return true;
        }

        var approved = await ApprovedHashesAsync([recipientId], cancellationToken);

        return approved.TryGetValue(recipientId, out var hash)
            && RecipientDestinationDefinition.Allows(hasher, hash, RecipientDestinationDefinition.Canonical(type, destination, headersJson));
    }

    // The approved hash of each recipient that exists, archived or not (null: not governed).
    private async Task<Dictionary<int, string?>> ApprovedHashesAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await context.Recipients
            .IgnoreQueryFilters()
            .Where(x => ids.Contains(x.Id))
            .Select(x =>
                new
                {
                    x.Id,
                    x.ApprovedDestinationHash
                })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(x => x.Id, x => x.ApprovedDestinationHash);
    }
}

/// <summary>What a delivery loaded of a recipient and will send: its id, type, and stored (encrypted) destination and headers.</summary>
internal sealed record RecipientCopy(int RecipientId, NotificationType Type, string StoredDestination, string? StoredHeadersJson)
{
    // The compiler-generated ToString prints every member; the secrets are printed redacted.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"RecipientId = {RecipientId}, Type = {Type}, StoredDestination = {RedactedPrint.Of(StoredDestination)}, StoredHeadersJson = {RedactedPrint.Of(StoredHeadersJson)}");

        return true;
    }
}
