using System.Reflection;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Adapters.Mail;

internal class EmailAdapter : IAdapter
{
    private readonly IEmailAdapter _emailAdapter;

    public NotificationType NotificationType => NotificationType.Email;

    public EmailAdapter(IEmailAdapter emailAdapter)
    {
        _emailAdapter = emailAdapter;
    }

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult)
    {
        var to = recipientQueryResult.RecipientDestination;
        var subject = $"[semantico] {recipientQueryResult.QueryResult.ProjectName} - {recipientQueryResult.QueryResult.SubscriptionName}";
        
        var htmlBody = Helpers.GenerateEmailContent(recipientQueryResult.QueryResult);

        await _emailAdapter.SendEmailAsync(to, subject, htmlBody, recipientQueryResult.QueryResultFile);
    }

    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int lastNotificationResultCount)
    {
        throw new NotSupportedException();
    }
}