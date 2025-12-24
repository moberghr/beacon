using System.Reflection;
using Semantico.Core.Adapters.Shared;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Adapters.Mail;

internal class EmailAdapter(IEmailAdapter emailAdapter) : IAdapter
{
    public NotificationType NotificationType => NotificationType.Email;

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int? lastNotificationResultCount)
    {
        var to = recipientQueryResult.RecipientDestination;
        var subject = $"{AdapterConstants.NotificationPrefix} {recipientQueryResult.QueryResult.DataSourceName} - {recipientQueryResult.QueryResult.SubscriptionName}";
        
        var htmlBody = Helpers.GenerateEmailContent(recipientQueryResult.QueryResult);

        await emailAdapter.SendEmailAsync(to, subject, htmlBody, recipientQueryResult.QueryResultFile);
    }
}