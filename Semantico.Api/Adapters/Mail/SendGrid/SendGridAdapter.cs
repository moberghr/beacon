using Microsoft.Extensions.Options;
using Semantico.Api.Worker;
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

    public async Task SendMailAsync(MessageRequest messageRequest, string email)
    {
        var senderEmail = new EmailAddress(_settings.SenderEmail, _settings.SenderName);
        var to = new EmailAddress(email);
        var subject = $"{messageRequest.ProjectName} - notification";
        var msg = MailHelper.CreateSingleEmail(senderEmail, to, subject, messageRequest.TotalRecords.ToString(), messageRequest.QueryResults);

        await _sendGridClient.SendEmailAsync(msg);
    }
}