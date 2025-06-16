using Microsoft.EntityFrameworkCore;
using Semantico.Core.Adapters;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers.File;
using Semantico.Core.Services;

namespace Semantico.Core.Worker.Services;

internal class JobService(SemanticoContext context, IQueryService queryService, INotificationService notificationService)
    : IJobService
{
    public async Task ExecuteQuery(int subscriptionId)
    {
        var subscription = await context.Subscriptions
            .Where(x => x.Id == subscriptionId)
            .FirstOrDefaultAsync();
            
        if (subscription == null)
        {
            return;
        }
        
        // Check if execution is within the allowed time window
        if (!IsWithinExecutionWindow(subscription))
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
        await context.SaveChangesAsync();

        // Only send notification if the status is NotificationSent
        if (executedQuery.NotificationStatus != NotificationStatus.NotificationSent)
        {
            return;
        }

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
    
    private static bool IsWithinExecutionWindow(Subscription subscription)
    {
        // If no execution window is defined, allow execution at any time
        if (!subscription.ExecutionWindowStartHour.HasValue || !subscription.ExecutionWindowEndHour.HasValue)
        {
            return true;
        }
        
        var currentHour = DateTime.Now.Hour;
        var startHour = subscription.ExecutionWindowStartHour.Value;
        var endHour = subscription.ExecutionWindowEndHour.Value;
        
        // Handle same-day window (e.g., 10:00 to 16:00)
        if (startHour <= endHour)
        {
            return currentHour >= startHour && currentHour < endHour;
        }
        
        // Handle overnight window (e.g., 22:00 to 06:00)
        return currentHour >= startHour || currentHour < endHour;
    }
}