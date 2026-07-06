using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Recipients;

internal sealed class GetRecipientsHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetRecipientsQuery, GetRecipientsResult>
{
    public async Task<GetRecipientsResult> Handle(GetRecipientsQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var entries = await context.Recipients
            .OrderBy(x => x.Name)
            .Select(x =>
                new RecipientEntry(
                    x.Id,
                    x.Name,
                    x.Description,
                    x.Destination,
                    (int)x.NotificationType,
                    x.HeadersJson,
                    x.BodyTemplate,
                    x.Subscriptions.Count))
            .ToListAsync(cancellationToken);

        return new GetRecipientsResult(entries);
    }
}

public record GetRecipientsQuery : IRequest<GetRecipientsResult>;

public record GetRecipientsResult(List<RecipientEntry> Entries);

public record RecipientEntry(
    int Id,
    string Name,
    string? Description,
    string Destination,
    int NotificationType,
    string? HeadersJson,
    string? BodyTemplate,
    int SubscriptionCount);

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
