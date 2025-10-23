using Microsoft.EntityFrameworkCore;
using Semantico.Core.Adapters;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers.File;
using Semantico.Core.Services;

namespace Semantico.Core.Worker.Services;

internal class JobService(IDbContextFactory<SemanticoContext> contextFactory, IQueryService queryService, INotificationService notificationService)
    : IJobService
{
    public async Task ExecuteQuery(int subscriptionId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var subscription = await context.Subscriptions
            .Where(x => x.Id == subscriptionId)
            .FirstOrDefaultAsync();

        if (subscription == null)
        {
            return;
        }

        var queryResult = await queryService.ExecuteQuery(subscriptionId, CancellationToken.None);

        // Set subscription specific parameters
        queryResult.ShowQuery = subscription.ShowQuery;
        queryResult.MaxRows = subscription.MaxRows;

        // Apply max rows limit if specified
        if (subscription.MaxRows.HasValue && subscription.MaxRows > 0)
        {
            queryResult.AllRecords = queryResult.AllRecords.Take(subscription.MaxRows.Value).ToList();
            queryResult.TopRecords = queryResult.TopRecords.Take(subscription.MaxRows.Value).ToList();
        }

        var lastExecutedQuery = context.QueryExecutionHistory
            .Where(x => x.SubscriptionId == subscriptionId)
            .OrderByDescending(x => x.CreatedTime)
            .Select(x =>
                new
                {
                    x.ResultCount
                })
            .FirstOrDefault();

        NotificationStatus status;

        lastExecutedQuery = null;

        // Use the explicit TimedOut flag
        if (queryResult.TimedOut)
        {
            status = NotificationStatus.Timeout;
        }
        else if (queryResult.TotalRecords == 0)
        {
            status = NotificationStatus.NoResults;
        }
        else if (lastExecutedQuery == null)
        {
            status = NotificationStatus.NotificationSent;
        }
        else if (queryResult.TotalRecords != lastExecutedQuery.ResultCount)
        {
            status = NotificationStatus.NotificationSent;
        }
        else
        {
            status = NotificationStatus.NotificationSilenced;
        }

        var executedQuery = new QueryExecutionHistory
        {
            SubscriptionId = subscriptionId,
            ResultCount = queryResult.TotalRecords,
            CompiledSql = queryResult.SqlQuery,
            NotificationStatus = status,
            ExecutionTimeMs = queryResult.ExecutionTimeMs
        };

        await context.QueryExecutionHistory.AddAsync(executedQuery);

        // Only send notification if the status is NotificationSent
        if (executedQuery.NotificationStatus != NotificationStatus.NotificationSent)
        {
            await context.SaveChangesAsync();
            return;
        }

        // Create Notification records for each recipient that was notified
        foreach (var recipient in queryResult.Recipients)
        {
            var notification = new Notification
            {
                RecipientId = recipient.RecipientId.Value,
                Type = recipient.NotificationType,
                SentAt = DateTime.UtcNow,
                Results = queryResult.SaveResults ? queryResult.QueryResults : null
            };
            
            executedQuery.Notifications.Add(notification);
        }
        
        await context.SaveChangesAsync();

        var recipientsQueryResults = new List<RecipientQueryResult>();
        var resultFiles = new Dictionary<FileType, QueryResultFile>();

        // Only create attachments if subscription has attachments enabled
        if (subscription.IncludeAttachment)
        {
            var fileTypes = queryResult.Recipients
                .Where(x => x.ResultAttachmentType.HasValue)
                .Select(x => x.ResultAttachmentType!.Value)
                .Distinct();

            foreach (var fileType in fileTypes)
            {
                resultFiles.Add(fileType, await ExportProvider.GetReport(fileType, queryResult.AllRecords));
            }
        }
        
        // TODO: refactor this to use sending Notifications table

        foreach (var recipient in queryResult.Recipients)
        {
            recipientsQueryResults.Add(new RecipientQueryResult
            {
                RecipientDestination = recipient.Destination,
                RecipientNotificationType = recipient.NotificationType,
                QueryResult = queryResult,
                QueryResultFile = subscription.IncludeAttachment && recipient.ResultAttachmentType.HasValue
                    ? resultFiles.GetValueOrDefault(recipient.ResultAttachmentType.Value)
                    : null
            });
        }

        foreach (var recipientQueryResult in recipientsQueryResults)
        {
            await notificationService.SendNotification(recipientQueryResult, lastExecutedQuery?.ResultCount);
        }
    }
}