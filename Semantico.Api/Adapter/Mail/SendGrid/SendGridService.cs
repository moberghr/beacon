using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Semantico.Api.Adapter.Mail.SendGrid;

public class SendGridService : IMailAdapter
{
    private readonly SendGridSettings _settings;
    private readonly ISendGridClient _sendGridClient;

    public SendGridService(IOptions<SendGridSettings> settings, ISendGridClient sendGridClient)
    {
        _settings = settings.Value;
        _sendGridClient = sendGridClient;
    }

    public async Task SendMailAsync(SendEmailRequest sendEmailRequest)
    {
        var senderEmail = new EmailAddress(_settings.SenderEmail, _settings.SenderName);
        var to = new EmailAddress(sendEmailRequest.To);
        var msg = MailHelper.CreateSingleEmail(senderEmail, to, sendEmailRequest.Subject, sendEmailRequest.Body, sendEmailRequest.Body);

        await _sendGridClient.SendEmailAsync(msg);
    }
}