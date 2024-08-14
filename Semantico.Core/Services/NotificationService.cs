using Microsoft.EntityFrameworkCore;
using Semantico.Core.Adapters;
using Semantico.Core.Adapters.Jira;
using Semantico.Core.Adapters.Mail;
using Semantico.Core.Adapters.Teams;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
using Semantico.Core.Models.QueryExecutionHistory;

namespace Semantico.Core.Services;

internal class NotificationService : INotificationService
{
    private readonly SemanticoContext _context;
    private readonly ITeamsAdapter _teamsAdapter;
    private readonly IMailAdapter _mailAdapter;
    private readonly IJiraAdapter _jiraAdapter;

    public NotificationService(
        SemanticoContext context,
        ITeamsAdapter teamsAdapter,
        IMailAdapter mailAdapter,
        IJiraAdapter jiraAdapter
    )
    {
        _context = context;
        _teamsAdapter = teamsAdapter;
        _mailAdapter = mailAdapter;
        _jiraAdapter = jiraAdapter;
    }

    public async Task SendNotificationAsync(NotificationType notificationType, RecipientQueryResult recipientQueryResult, int? lastExecutedQueryResultCount)
    {
        switch (notificationType)
        {
            case NotificationType.Email:
                await _mailAdapter.SendNotificationAsync(recipientQueryResult);
                break;

            case NotificationType.Teams:
                await _teamsAdapter.SendNotificationAsync(recipientQueryResult);
                break;

            case NotificationType.Jira:
                if (lastExecutedQueryResultCount.HasValue)
                {
                    await _jiraAdapter.SendNotificationAsync(recipientQueryResult, lastExecutedQueryResultCount.Value);
                }
                else
                {
                    await _jiraAdapter.SendNotificationAsync(recipientQueryResult);
                }

                break;

            default:
                throw new SemanticoException("Invalid notification type");
        }
    }

    public async Task<QueryExecutionHistoryListData> GetQueryExecutionHistoryAsync(int subscriptionId, int? pageSize,
            int? lastQueryExecutionHistoryId, bool? notificationSent, CancellationToken cancellationToken)
    {
        var queryExecutionHistory = await _context.QueryExecutionHistory
            .Where(x => x.SubscriptionId == subscriptionId)
            .WhereIf(lastQueryExecutionHistoryId.HasValue, x => x.Id < lastQueryExecutionHistoryId)
            .WhereIf(notificationSent.HasValue, x => x.NotificationSent == notificationSent)
            .OrderByDescending(x => x.Id)
            .TakeIf(pageSize.HasValue, pageSize)
            .Select(x =>
                new QueryExecutionHistoryData
                {
                    QueryExecutionHistoryId = x.Id,
                    Recipient = x.Recipient,
                    NotificationType = x.NotificationType,
                    ResultCount = x.ResultCount
                })
            .ToListAsync(cancellationToken);

        return new QueryExecutionHistoryListData
        {
            LastQueryExecutionHistoryId = queryExecutionHistory.LastOrDefault()?.QueryExecutionHistoryId,
            QueryExecutionHistory = queryExecutionHistory
        };
    }
}

public interface INotificationService
{
    public Task SendNotificationAsync(NotificationType notificationType, RecipientQueryResult recipientQueryResult, int? lastExecutedQueryResultCount);

    Task<QueryExecutionHistoryListData> GetQueryExecutionHistoryAsync(int subscriptionId, int? pageSize,
        int? lastQueryExecutionHistoryId, bool? notificationSent, CancellationToken cancellationToken);
}
