using Microsoft.EntityFrameworkCore;
using Semantico.Core.Adapters;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Helpers;
using Semantico.Core.Models.Queries;
using Semantico.Core.Models.Subscriptions;
using Semantico.Core.Services;
using Semantico.Core.Validators;
using Semantico.Core.Worker.Repositories;
using System.Text.Json;

namespace Semantico.Core.Worker.Services;

internal class JobService : IJobService
{
    private readonly SemanticoContext _context;
    private readonly IJobRepository _jobRepository;
    private readonly INotificationService _notificationService;

    public JobService(SemanticoContext context, IJobRepository jobRepository, INotificationService notificationService)
    {
        _context = context;
        _jobRepository = jobRepository;
        _notificationService = notificationService;
    }

    public async Task ExecuteQuery(int subscriptionId)
    {
        var subscription = await _context.Subscriptions
            .Include(x => x.Parameters)
            .Where(x => x.Id == subscriptionId)
            .Select(x =>
                new
                {
                    x.Id,
                    x.NotificationType,
                    x.QueryId,
                    x.Recipient,
                    x.CronExpression,
                    x.Query.SqlValue,
                    x.Query.Name,
                    Project = new
                    {
                        x.Query.Project.Name,
                        x.Query.Project.ConnectionString,
                        x.Query.Project.DatabaseEngineType
                    },
                    Parameters = x.Parameters.Select(y =>
                        new SubscriptionParamaterData
                        {
                            QueryPlaceholder = y.QueryPlaceholder,
                            Value = y.Value
                        }).ToList()
                })
            .SingleAsync();
        
        var sql = QueryHelper.CompileSql(subscription.SqlValue, subscription.Parameters);

        QueryValidator.CheckForFlaggedWords(sql);

        var dbQueryResult = await _jobRepository.ExecuteQueryAsync(subscription.Project.DatabaseEngineType, subscription.Project.ConnectionString, sql);

        // We will only send the top 10 rows in a notification.
        var messageRows = dbQueryResult.Take(10).ToList();

        var queryResult = new QueryResult
        {
            QueryResults = JsonSerializer.Serialize(messageRows),
            TotalRecords = dbQueryResult.Count(),
            ProjectName = subscription.Project.Name,
            SqlQuery = sql,
        };

        var recipientQueryResult = new RecipientQueryResult
        {
            SubscriptionName = subscription.Name,
            Recipient = subscription.Recipient,
            QueryResult = queryResult
        };

        var lastExecutedQuery = _context.QueryExecutionHistory
            .Where(x => x.SubscriptionId == subscriptionId)
            .OrderByDescending(x => x.CreatedTime)
            .Select(x =>
                new
                {
                    x.ResultCount
                })
            .FirstOrDefault();

        var initialNotification = lastExecutedQuery == null && recipientQueryResult.QueryResult.TotalRecords != 0;
        var differentResults = lastExecutedQuery != null && recipientQueryResult.QueryResult.TotalRecords != lastExecutedQuery.ResultCount;

        // if a previous notification wasn't sent and there are some query results or
        // if a previous notification was sent, and the current result is the same we won't send a notification.

        var executedQuery = new QueryExecutionHistory
        {
            Recipient = recipientQueryResult.Recipient,
            NotificationType = subscription.NotificationType,
            SubscriptionId = subscriptionId,
            ResultCount = recipientQueryResult.QueryResult.TotalRecords,
            CompiledSql = recipientQueryResult.QueryResult.SqlQuery,
            NotificationSent = initialNotification || differentResults
        };

        await _context.QueryExecutionHistory.AddAsync(executedQuery);
        await _context.SaveChangesAsync();

        if (executedQuery.NotificationSent == false)
        {
            return;
        }

        await _notificationService.SendNotificationAsync(subscription.NotificationType, recipientQueryResult, lastExecutedQuery?.ResultCount);
    }
}