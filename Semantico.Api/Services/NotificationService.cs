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
        switch (notificationType)
        {
            case NotificationType.Email:
                await _mailAdapter.SendNotificationAsync(subscriptionId, recipientQueryResult);
                break;

            case NotificationType.Teams:
                await _teamsAdapter.SendNotificationAsync(subscriptionId, recipientQueryResult);
                break;

            case NotificationType.Jira:
                await _jiraAdapter.SendNotificationAsync(subscriptionId, recipientQueryResult);
                break;

            default:
                throw new SemanticoException("Invalid notification type");
        }

        var notification = new Notification
        {
            Recipient = recipientQueryResult.Recipient,
            NotificationType = notificationType,
            SubscriptionId = subscriptionId,
            ResultCount = recipientQueryResult.QueryResult.TotalRecords
        };

        await _context.Notifications.AddAsync(notification);
        await _context.SaveChangesAsync();
    }
}

public interface INotificationService
{
    public Task SendNotificationAsync(int subscriptionId, NotificationType notificationType, RecipientQueryResult recipientQueryResult);
}
