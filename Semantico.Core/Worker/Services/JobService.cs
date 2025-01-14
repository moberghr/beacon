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

        var recipientsQueryResults = new List<RecipientQueryResult>();
        var resultFiles = new Dictionary<FileType, QueryResultFile>();

        var fileTypes = queryResult.Recipients
            .Where(x => x.ResultAttachmentType.HasValue)
            .Select(x => x.ResultAttachmentType!.Value)
            .Distinct();

        foreach (var fileType in fileTypes)
        {
            resultFiles.Add(fileType, await ExportProvider.GetReport(fileType, queryResult.AllRecords));
        }

        foreach (var recipient in queryResult.Recipients)
        {
            recipientsQueryResults.Add(new RecipientQueryResult
            {
                RecipientDestination = recipient.Destination,
                RecipientNotificationType = recipient.NotificationType,
                QueryResult = queryResult,
                QueryResultFile = recipient.ResultAttachmentType.HasValue 
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