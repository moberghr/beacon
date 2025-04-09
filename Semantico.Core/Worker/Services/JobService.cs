using Microsoft.EntityFrameworkCore;
using Semantico.Core.Adapters;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers.File;
using Semantico.Core.Services;

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
        
        var subscription = await _context.Subscriptions
            .Where(x => x.Id == subscriptionId)
            .FirstOrDefaultAsync();
            
        if (subscription == null)
        {
            return;
        }
        
        // Set subscription specific parameters
        queryResult.ShowQuery = subscription.ShowQuery;
        queryResult.MaxRows = subscription.MaxRows;
        
        // Apply max rows limit if specified
        if (subscription.MaxRows.HasValue && subscription.MaxRows > 0)
        {
            queryResult.AllRecords = queryResult.AllRecords.Take(subscription.MaxRows.Value).ToList();
            queryResult.TopRecords = queryResult.TopRecords.Take(subscription.MaxRows.Value).ToList();
        }

        var lastExecutedQuery = _context.QueryExecutionHistory
                .Where(x => x.SubscriptionId == subscriptionId)
                .OrderByDescending(x => x.CreatedTime)
                .Select(x =>
                    new
                    {
                        x.ResultCount
                    })
                .FirstOrDefault();

        NotificationStatus status;
        
        if (queryResult.TotalRecords == 0)
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
            NotificationStatus = status
        };

        await _context.QueryExecutionHistory.AddAsync(executedQuery);
        await _context.SaveChangesAsync();

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
            await _notificationService.SendNotification(recipientQueryResult, lastExecutedQuery?.ResultCount);
        }
    }
}