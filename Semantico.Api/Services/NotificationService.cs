using Microsoft.EntityFrameworkCore;
using Semantico.Api.Adapters;
using Semantico.Api.Adapters.Jira;
using Semantico.Api.Adapters.Mail;
using Semantico.Api.Adapters.Teams;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Data.Enums;
using Semantico.Api.Types;

namespace Semantico.Api.Services;

public class NotificationService : INotificationService
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

    public async Task SendNotificationAsync(int subscriptionId, NotificationType notificationType, RecipientQueryResult recipientQueryResult)
    {
        var lastExecutedQuery = _context.QueryExecutionHistory
            .Where(x => x.SubscriptionId == subscriptionId)
            .OrderByDescending(x => x.CreatedTime)
            .Select(x =>
                new
                {
                    x.ResultCount
                })
            .FirstOrDefault();

        var executedQuery = new QueryExecutionHistory
        {
            Recipient = recipientQueryResult.Recipient,
            NotificationType = notificationType,
            SubscriptionId = subscriptionId,
            ResultCount = recipientQueryResult.QueryResult.TotalRecords,
            CompiledSql = recipientQueryResult.QueryResult.SqlQuery,
            NotificationSent = true
        };

        await _context.QueryExecutionHistory.AddAsync(executedQuery);

        bool noNewRecords = (lastExecutedQuery == null && recipientQueryResult.QueryResult.TotalRecords == 0);
        bool previousRecordCountIsTheSame = (lastExecutedQuery != null && recipientQueryResult.QueryResult.TotalRecords != lastExecutedQuery.ResultCount);

        // if a previous notification wasn't sent and there are no query results or
        // if a previous notification was sent, and the current result is the same we won't send a notification.
        if (noNewRecords || previousRecordCountIsTheSame)
        {
            executedQuery.NotificationSent = false;
            await _context.SaveChangesAsync();
            return;
        }

        switch (notificationType)
        {
            case NotificationType.Email:
                await _mailAdapter.SendNotificationAsync(recipientQueryResult);
                break;

            case NotificationType.Teams:
                await _teamsAdapter.SendNotificationAsync(recipientQueryResult);
                break;

            case NotificationType.Jira:
                if (lastExecutedQuery != null)
                {
                    await _jiraAdapter.SendNotificationAsync(recipientQueryResult, lastExecutedQuery.ResultCount);
                }
                else
                {
                    await _jiraAdapter.SendNotificationAsync(recipientQueryResult);
                }

                break;

            default:
                throw new SemanticoException("Invalid notification type");
        }

        await _context.SaveChangesAsync();
    }
}

public interface INotificationService
{
    public Task SendNotificationAsync(int subscriptionId, NotificationType notificationType, RecipientQueryResult recipientQueryResult);
}
