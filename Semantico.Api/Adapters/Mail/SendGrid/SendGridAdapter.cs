using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Semantico.Api.Adapters.Mail.SendGrid;

public class SendGridAdapter : IMailAdapter
{
    private readonly SendGridSettings _settings;
    private readonly ISendGridClient _sendGridClient;

    public SendGridAdapter(IOptions<SendGridSettings> settings, ISendGridClient sendGridClient)
    {
        _settings = settings.Value;
        _sendGridClient = sendGridClient;
    }

    public async Task SendMailAsync(RecipientQueryResult recipientQueryResult)
    {
        var senderEmail = new EmailAddress(_settings.SenderEmail, _settings.SenderName);
        var to = new EmailAddress(recipientQueryResult.Recipient);
        var subject = $"{recipientQueryResult.QueryResult.ProjectName} - notification";
        var plainTextContent = $"Sql Query: {recipientQueryResult.QueryResult.SqlQuery} \nQuery executed successfuly with total records of: {recipientQueryResult.QueryResult.TotalRecords}";
        var msg = MailHelper.CreateSingleEmail(senderEmail, to, subject, plainTextContent, recipientQueryResult.QueryResult.QueryResults);

        await _sendGridClient.SendEmailAsync(msg);
    }
}