using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Notifications;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Recipients;

/// <summary>
/// Every reader may list recipients by name and type, so they can attach them to subscriptions and data contracts.
/// Only those who change recipients, an Admin or a SecurityAdmin, see destinations and headers, and only masked
/// (<see cref="RecipientSecrets"/>), with a flag when a stored value cannot be decrypted and must be entered again; for
/// anyone else they, and the body template, are null.
/// </summary>
internal sealed class GetRecipientsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconPermissionAccessor permissionAccessor,
    RecipientSecretEditor secretEditor)
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

        return page.Map(x =>
        {
            var masked = seesDestinations
                ? secretEditor.ReadMasked((NotificationType)x.NotificationType, x.StoredDestination, x.StoredHeadersJson)
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
