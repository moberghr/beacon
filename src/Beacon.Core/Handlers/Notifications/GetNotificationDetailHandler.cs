using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using Beacon.Core.Models.QueryExecutionHistory;
using MediatR;

namespace Beacon.Core.Handlers.Notifications;

internal sealed class GetNotificationDetailHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetNotificationDetailQuery, GetNotificationDetailResult>
{
    public async Task<GetNotificationDetailResult> Handle(GetNotificationDetailQuery request, CancellationToken cancellationToken)
    {
        // A run outside the caller's projects reads as not found, like a missing one.
        var scope = await StoredRunAccess.ScopeOfAsync(projectAccess, cancellationToken);
        var data = await Read(request.NotificationId, scope, cancellationToken);

        if (data == null)
        {
            return new GetNotificationDetailResult(null);
        }

        var entry = new NotificationDetailEntry(
            data.Id,
            data.QueryId,
            data.QueryName,
            data.SubscriptionId,
            data.RecipientName,
            data.Type,
            data.NotificationStatus,
            data.CreatedTime,
            data.SentAt,
            data.ExecutionTimeMs,
            data.ResultCount,
            data.Results);

        return new GetNotificationDetailResult(entry);
    }

    internal async Task<NotificationDetailsData?> Read(int queryExecutionHistoryId, StoredRunScope scope, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The notifications list shows one row per run; clicking through passes that run's id, so the detail is resolved
        // by run id and shows its first notification's recipient and type.
        //
        // Two-step load: scalar subqueries over an empty Notifications collection made EF/Npgsql materialize the whole
        // projected row as null, surfacing as a 404 for runs that have no notifications yet.
        var details = await context.QueryExecutionHistory
            .IgnoreQueryFilters()
            .Where(x => x.Id == queryExecutionHistoryId)
            .WhereReadableWithin(context, scope)
            .Select(x =>
                new NotificationDetailsData
                {
                    Id = x.Id,
                    CreatedTime = x.CreatedTime,
                    SentAt = x.CreatedTime,
                    Type = default,
                    Results = x.Results,
                    RecipientName = string.Empty,
                    QueryName = x.Subscription.Query.Name,
                    QueryId = x.Subscription.QueryId,
                    SubscriptionId = x.SubscriptionId,
                    ExecutionTimeMs = x.ExecutionTimeMs,
                    ResultCount = x.ResultCount,
                    NotificationStatus = x.NotificationStatus
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (details == null)
        {
            return null;
        }

        // The run passed the scope check above: a delivery stays part of that record after its recipient is archived.
        var firstNotification = await context.Notifications
            .IgnoreQueryFilters()
            .Where(x => x.QueryExecutionHistoryId == queryExecutionHistoryId)
            .OrderBy(x => x.Id)
            .Select(x =>
                new
                {
                    x.SentAt,
                    x.Type,
                    x.Results,
                    RecipientName = x.Recipient.Name
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (firstNotification != null)
        {
            details.SentAt = firstNotification.SentAt;
            details.Type = firstNotification.Type;
            details.Results = details.Results ?? firstNotification.Results;
            details.RecipientName = firstNotification.RecipientName ?? string.Empty;
        }

        return details;
    }
}

public record GetNotificationDetailQuery(int NotificationId) : IRequest<GetNotificationDetailResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Notification(NotificationId), ProjectAction.Read)
    ];
}

public record GetNotificationDetailResult(NotificationDetailEntry? Entry);

public record NotificationDetailEntry(
    int Id,
    int QueryId,
    string QueryName,
    int SubscriptionId,
    string RecipientName,
    NotificationType Type,
    NotificationStatus Status,
    DateTime CreatedTime,
    DateTime SentAt,
    double ExecutionTimeMs,
    int? ResultCount,
    string? Results);
