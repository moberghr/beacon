using Microsoft.EntityFrameworkCore;
using Semantico.Core.Adapters;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Helpers;
using Semantico.Core.Models.Recipients;
using Semantico.Core.Models.Subscriptions;
using Semantico.Core.Services;
using Semantico.Core.Validators;
using Semantico.Core.Worker.Repositories;
using System.Text.Json;

namespace Semantico.Core.Worker.Services;

internal class JobService : IJobService
{
    private readonly SemanticoContext _context;
    private readonly IQueryService _queryService;
    private readonly INotificationService _notificationService;

    public JobService(SemanticoContext context, IQueryService queryService, INotificationService notificationService)
    {
        _context = context;
        _queryService = queryService;
        _notificationService = notificationService;
    }

    public async Task ExecuteQuery(int subscriptionId)
    {
        var queryResult = await _queryService.ExecuteQuery(subscriptionId, CancellationToken.None);
        
        var recipientsQueryResults = queryResult.Recipients
            .Select(x => new RecipientQueryResult
            {
                SubscriptionName = queryResult.SubscriptionName,
                RecipientDestination = x.Destination,
                RecipientNotificationType = x.NotificationType,
                QueryResult = queryResult
            })
            .ToList();

        var lastExecutedQuery = _context.QueryExecutionHistory
                .Where(x => x.SubscriptionId == subscriptionId)
                .OrderByDescending(x => x.CreatedTime)
                .Select(x =>
                    new
                    {
                        x.ResultCount
                    })
                .FirstOrDefault();

        var initialNotification = lastExecutedQuery == null && queryResult.TotalRecords != 0;
        var differentResults = lastExecutedQuery != null && queryResult.TotalRecords != lastExecutedQuery.ResultCount;

        var executedQuery = new QueryExecutionHistory
        {
            SubscriptionId = subscriptionId,
            ResultCount = queryResult.TotalRecords,
            CompiledSql = queryResult.SqlQuery,
            NotificationSent = initialNotification || differentResults
        };

        await _context.QueryExecutionHistory.AddAsync(executedQuery);
        await _context.SaveChangesAsync();

        // if a previous notification wasn't sent and there are some query results or
        // if a previous notification was sent, and the current result is the same we won't send a notification.
        if (executedQuery.NotificationSent == false)
        {
            return;
        }

        foreach (var recipientQueryResult in recipientsQueryResults)
        {
            await _notificationService.SendNotification(recipientQueryResult, lastExecutedQuery?.ResultCount);
        }
    }
}