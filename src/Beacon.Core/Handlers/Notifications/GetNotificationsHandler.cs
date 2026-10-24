using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.QueryExecutionHistory;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Notifications;

internal sealed class GetNotificationsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetNotificationsQuery, PagedList<NotificationEntry>>
{
    public async Task<PagedList<NotificationEntry>> Handle(GetNotificationsQuery request, CancellationToken cancellationToken)
    {
        var serviceRequest = new GetQueryExecutionHistoryRequest
        {
            Page = request.Page,
            PageSize = request.PageSize,
            Sort = request.Sort,
            NotificationStatus = request.Status,
            SubscriptionId = request.SubscriptionId,
            Scope = await StoredRunAccess.ScopeOfAsync(projectAccess, cancellationToken),
        };

        var data = await Read(serviceRequest, cancellationToken);

        return data.Map(x =>
            new NotificationEntry(
                x.QueryExecutionHistoryId,
                x.SubscriptionId,
                x.QueryName,
                x.NotificationStatus,
                x.ResultCount,
                x.ExecutionTimeMs,
                x.CreatedTime,
                x.AiActorId,
                x.AiActorName,
                x.Comment,
                x.Notifications
                    .Select(y => y.RecipientName)
                    .ToList()));
    }

    internal async Task<PagedList<QueryExecutionHistoryData>> Read(GetQueryExecutionHistoryRequest request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var page = await context.QueryExecutionHistory
            .WhereIf(request.SubscriptionId.HasValue, x => x.SubscriptionId == request.SubscriptionId)
            .WhereIf(request.NotificationStatus.HasValue, x => x.NotificationStatus == request.NotificationStatus)
            .WhereReadableWithin(context, request.Scope)
            .Select(x =>
                new QueryExecutionHistoryData
                {
                    QueryExecutionHistoryId = x.Id,
                    Notifications = x.Notifications
                        .Select(y =>
                            new NotificationData
                            {
                                Id = y.Id,
                                Created = y.CreatedTime,
                                NotificationType = y.Type,
                                RecipientName = y.Recipient.Name,
                                SentAt = y.SentAt
                            })
                        .ToList(),
                    ResultCount = x.ResultCount,
                    CreatedTime = x.CreatedTime,
                    NotificationStatus = x.NotificationStatus,
                    QueryName = x.Subscription.Query.Name,
                    SubscriptionId = x.SubscriptionId,
                    ExecutionTimeMs = x.ExecutionTimeMs,
                    Comment = x.Comment,
                    AiActorId = x.Subscription.AiActorId,
                    AiActorName = x.Subscription.AiActor != null ? x.Subscription.AiActor.Name : null
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdTime", tiebreaker: "queryExecutionHistoryId");

        // A run's stored comment can hold failure detail recorded by earlier versions: only a recorded reason leaves.
        foreach (var item in page.Items)
        {
            item.Comment = NotificationFailureReasons.Displayable(item.Comment);
        }

        return page;
    }
}

/// <summary>Newest first unless <c>sort</c> says otherwise; sortable by any scalar column of the row.</summary>
public record GetNotificationsQuery : ListRequest, IRequest<PagedList<NotificationEntry>>, IProjectFilteredRequest
{
    public NotificationStatus? Status { get; init; }

    [ProjectScopeExempt("filter only: the list is scope-filtered")]

    public int? SubscriptionId { get; init; }
}

/// <summary>
/// A run in the notifications list. <c>Comment</c> is the recorded failure reason (<c>NotificationFailureReasons</c>), or
/// the generic reason in place of any other stored detail.
/// </summary>
public record NotificationEntry(
    int Id,
    int SubscriptionId,
    string QueryName,
    NotificationStatus Status,
    int ResultCount,
    double ExecutionTimeMs,
    DateTime CreatedTime,
    int? AiActorId,
    string? AiActorName,
    string? Comment,
    List<string> RecipientNames);
