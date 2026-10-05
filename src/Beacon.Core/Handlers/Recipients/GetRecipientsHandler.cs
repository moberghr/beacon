using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Recipients;

internal sealed class GetRecipientsHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetRecipientsQuery, PagedList<RecipientEntry>>
{
    public async Task<PagedList<RecipientEntry>> Handle(GetRecipientsQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.Recipients.AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            query = query.Where(x =>
                x.Name.Contains(request.Search) ||
                x.Destination.Contains(request.Search) ||
                (x.Description != null && x.Description.Contains(request.Search)));
        }

        return await query
            .Select(x =>
                new RecipientEntry
                {
                    Id = x.Id,
                    Name = x.Name,
                    Description = x.Description,
                    Destination = x.Destination,
                    NotificationType = (int)x.NotificationType,
                    HeadersJson = x.HeadersJson,
                    BodyTemplate = x.BodyTemplate,
                    SubscriptionCount = x.Subscriptions.Count,
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "name");
    }
}

/// <summary>Alphabetical unless <c>sort</c> says otherwise; <c>search</c> matches name, destination and description.</summary>
public record GetRecipientsQuery : ListRequest, IRequest<PagedList<RecipientEntry>>
{
    public string? Search { get; init; }
}

public record RecipientEntry
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Destination { get; init; } = string.Empty;

    public int NotificationType { get; init; }

    public string? HeadersJson { get; init; }

    public string? BodyTemplate { get; init; }

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
