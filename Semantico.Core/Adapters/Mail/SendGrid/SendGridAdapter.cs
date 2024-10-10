using Microsoft.Extensions.Options;
using Semantico.Core.Data.Enums;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Semantico.Core.Adapters.Mail.SendGrid;

internal class SendGridAdapter : IAdapter
{
    private readonly SendGridSettings _settings;
    private readonly ISendGridClient _sendGridClient;

    public NotificationType NotificationType => NotificationType.Email;

    public SendGridAdapter(IOptions<SendGridSettings> settings, ISendGridClient sendGridClient)
    {
        _settings = settings.Value;
        _sendGridClient = sendGridClient;
    }

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult)
    {
        var senderEmail = new EmailAddress(_settings.SenderEmail, _settings.SenderName);
        var to = new EmailAddress(recipientQueryResult.Recipient);
        var subject = $"{recipientQueryResult.QueryResult.ProjectName} - {recipientQueryResult.SubscriptionName}";
        var plainTextContent = $"Sql Query: {recipientQueryResult.QueryResult.SqlQuery} \nQuery executed successfuly with total records of: {recipientQueryResult.QueryResult.TotalRecords}";
        var msg = MailHelper.CreateSingleEmail(senderEmail, to, subject, plainTextContent, recipientQueryResult.QueryResult.QueryResults);

        await _sendGridClient.SendEmailAsync(msg);
    }

    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int lastNotificationResultCount)
    {
        throw new NotSupportedException();
    }
}