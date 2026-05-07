using System.Reflection;
using Beacon.Core.Adapters.Shared;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Adapters.Mail;

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