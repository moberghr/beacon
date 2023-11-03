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
    private readonly ITeamsAdapter _teamsAdapter;
    private readonly IMailAdapter _mailAdapter;
    private readonly IJiraAdapter _jiraAdapter;

    public NotificationService(
        ITeamsAdapter teamsAdapter,
        IMailAdapter mailAdapter,
        IJiraAdapter jiraAdapter
    )
    {
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
}

public interface INotificationService
{
    public Task SendNotificationAsync(NotificationType notificationType, RecipientQueryResult recipientQueryResult, int? lastExecutedQueryResultCount);
}
