using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Notifications;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Recipients;

/// <summary>
/// Every reader may list recipients by name and type, so they can attach them to subscriptions and data contracts.
/// Only those who change recipients, an Admin or a SecurityAdmin, see destinations and headers, and only masked
/// (<see cref="RecipientSecrets"/>), with a flag when a stored value cannot be decrypted and must be entered again; for
/// anyone else they, and the body template, are null. Those who change recipients also see short keyed fingerprints of
/// where each one delivers and of each header value, the same an Approver is shown, to compare out of band. Every reader
/// sees which recipients deliveries skip because where they deliver waits for approval, and the pending request of that
/// change, or else the last decision on it.
/// </summary>
internal sealed class GetRecipientsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconPermissionAccessor permissionAccessor,
    RecipientSecretEditor secretEditor,
    RecipientDeliveryApproval deliveryApproval,
    RecipientSecretProtector protector,
    RecipientDestinationHasher hasher)
    : IRequestHandler<GetRecipientsQuery, PagedList<RecipientEntry>>
{
    public async Task<PagedList<RecipientEntry>> Handle(GetRecipientsQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.Recipients.AsQueryable();

        // Destinations are encrypted at rest, so search covers the name and description only.
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            query = query.Where(x =>
                x.Name.Contains(request.Search) ||
                (x.Description != null && x.Description.Contains(request.Search)));
        }

        var page = await query
            .Select(x =>
                new RecipientRow
                {
                    Id = x.Id,
                    Name = x.Name,
                    Description = x.Description,
                    StoredDestination = x.Destination,
                    NotificationType = (int)x.NotificationType,
                    StoredHeadersJson = x.HeadersJson,
                    StoredBodyTemplate = x.BodyTemplate,
                    SubscriptionCount = x.Subscriptions.Count,
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "name");

        // Who may change recipients, now. API keys hold no permission.
        var seesDestinations = (await permissionAccessor.GetCurrentAsync(cancellationToken)).Holds(BeaconPermissions.SecurityAdmin);

        // Only while the workflow covers recipients does anything wait for approval.
        var ids = page.Items
            .Select(x => x.Id)
            .ToList();
        var held = await deliveryApproval.HeldAsync(ids, cancellationToken);
        var pending = deliveryApproval.Covers && ids.Count > 0
            ? await context.ChangeApprovalRequests
                .Where(x => x.Kind == ChangeApprovalKind.RecipientDestination)
                .Where(x => x.Status == ApprovalStatus.Pending)
                .Where(x => ids.Contains(x.ResourceId))
                .Select(x =>
                    new
                    {
                        x.ResourceId,
                        x.Id
                    })
                .ToListAsync(cancellationToken)
            : [];
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

        return page.Map(x =>
        {
            var masked = seesDestinations
                ? secretEditor.ReadMasked((NotificationType)x.NotificationType, x.StoredDestination, x.StoredHeadersJson)
                : null;
            var view = seesDestinations
                ? RecipientDestinationDefinition.ViewOfStored(protector, hasher, (NotificationType)x.NotificationType, x.StoredDestination, x.StoredHeadersJson)
                : null;

            return new RecipientEntry
            {
                Id = x.Id,
                Name = x.Name,
                Description = x.Description,
                Destination = masked?.Destination,
                NotificationType = x.NotificationType,
                HeadersJson = masked?.HeadersJson,
                BodyTemplate = seesDestinations ? x.StoredBodyTemplate : null,
                SecretsUnreadable = masked?.Unreadable == true,
                SubscriptionCount = x.SubscriptionCount,
                DefinitionFingerprint = view?.Fingerprint,
                HeaderFingerprints = view?.Headers,
                AwaitingApproval = held.Contains(x.Id),
                PendingApprovalRequestId = pendingByRecipient.TryGetValue(x.Id, out var requestId) ? requestId : null,
                LastApprovalDecision = decided.GetValueOrDefault(x.Id),
            };
        });
    }

    // The stored (encrypted) columns and the body template never leave the handler unmasked. They are internal so the
    // list sort, which resolves public properties only, cannot order by them; the public ones match RecipientEntry's
    // names.
    private sealed record RecipientRow
    {
        public int Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string? Description { get; init; }

        internal string StoredDestination { get; init; } = string.Empty;

        public int NotificationType { get; init; }

        internal string? StoredHeadersJson { get; init; }

        internal string? StoredBodyTemplate { get; init; }

        public int SubscriptionCount { get; init; }
    }
}

/// <summary>Alphabetical unless <c>sort</c> says otherwise; <c>search</c> matches name and description.</summary>
[ProjectScopeExempt("recipients are project-less; destinations are shown, masked, to Admins and SecurityAdmins only")]
public record GetRecipientsQuery : ListRequest, IRequest<PagedList<RecipientEntry>>
{
    public string? Search { get; init; }
}

/// <summary>
/// A recipient as listed. <see cref="Destination"/> and <see cref="HeadersJson"/> are masked and returned to Admins
/// only (null otherwise); sending a masked value back on update keeps the stored secret.
/// </summary>
public record RecipientEntry
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? Destination { get; init; }

    public int NotificationType { get; init; }

    public string? HeadersJson { get; init; }

    public string? BodyTemplate { get; init; }

    /// <summary>
    /// Admins only: a stored destination or header value cannot be decrypted (for example after an encryption key
    /// change) and must be entered again before the recipient can be saved or delivered to.
    /// </summary>
    public bool SecretsUnreadable { get; init; }

    public int SubscriptionCount { get; init; }

    /// <summary>
    /// Admins and SecurityAdmins only: a short keyed fingerprint of where the recipient delivers (type, destination and
    /// headers), the one an Approver is shown for it; equal fingerprints mean the same destination. Null when it cannot
    /// be read, and for anyone else.
    /// </summary>
    public string? DefinitionFingerprint { get; init; }

    /// <summary>Admins and SecurityAdmins only: each custom header's name with a fingerprint of its value (never the value).</summary>
    public IReadOnlyList<RecipientHeaderView>? HeaderFingerprints { get; init; }

    /// <summary>
    /// While the approval workflow covers recipients: where this recipient delivers waits for approval (it was created or
    /// changed and is not approved yet, or its change was rejected), so deliveries skip it.
    /// </summary>
    public bool AwaitingApproval { get; init; }

    /// <summary>The pending change request of where this recipient delivers, if one waits; an Approver opens it.</summary>
    public int? PendingApprovalRequestId { get; init; }

    /// <summary>
    /// While the recipient waits and no request of it is pending: the last decision on where it delivers (a rejection,
    /// with the reviewer's comment: change it and save again).
    /// </summary>
    public ChangeApprovalOutcome? LastApprovalDecision { get; init; }
}

// Keep the enum value list close to the API so the React side can render
// labels without re-deriving them — the enum int is the wire format.
public static class RecipientNotificationTypes
{
    public static readonly Dictionary<NotificationType, string> Names = new()
    {
        [NotificationType.Teams] = "Teams",
        [NotificationType.Email] = "Email",
        [NotificationType.Jira] = "Jira",
        [NotificationType.Slack] = "Slack",
        [NotificationType.Webhook] = "Webhook",
    };
}
