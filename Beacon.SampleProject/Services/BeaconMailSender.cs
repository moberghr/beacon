using System.Net;
using System.Net.Mail;
using Beacon.Core.Adapters;
using Beacon.Core.Adapters.Mail;

namespace Beacon.SampleProject.Services;

public class BeaconMailSender : IEmailAdapter
{
    public async Task SendEmailAsync(string to, string subject, string body, QueryResultFile? file)
    {
        // Configure the SMTP client
        var smtpClient = new SmtpClient("smtp.mandrillapp.com")
        {
            Port = 465,
            EnableSsl = true,
            Credentials = new NetworkCredential("dev@netgiro.is", "[REMOVED]")
        };

        // Create the email message
        var mailMessage = new MailMessage
        {
            From = new MailAddress("dev@netgiro.is"),
            Subject = subject,
            Body = body,
            IsBodyHtml = true,
        };
        mailMessage.To.Add(to);

        // Add attachment if provided
        if (file != null)
        {
            var attachment = new Attachment(new MemoryStream(file.Data), file.Name, file.ContentType);
            mailMessage.Attachments.Add(attachment);
        }

        // Send the email
        await smtpClient.SendMailAsync(mailMessage);
    }
}