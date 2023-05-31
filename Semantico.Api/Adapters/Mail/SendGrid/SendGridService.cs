using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Semantico.Api.Adapters.Mail.SendGrid;

public class SendGridService : IMailAdapter
{
    private readonly SendGridSettings _settings;

    public SendGridService(IOptions<SendGridSettings> settings)
    {
        _settings = settings.Value;
    }

    public async Task SendMailAsync(SendEmailRequest sendEmailRequest)
    {
        var client = new SendGridClient(_settings.Apikey);

        var senderEmail = new EmailAddress(_settings.SenderEmail, _settings.SenderName);
        var to = new EmailAddress(sendEmailRequest.To);
        var msg = MailHelper.CreateSingleEmail(senderEmail, to, sendEmailRequest.Subject, sendEmailRequest.Body, sendEmailRequest.Body);

        await client.SendEmailAsync(msg);
    }
}