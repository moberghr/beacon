using System.Net;
using System.Net.Mail;
using Beacon.Core.Adapters;
using Beacon.Core.Adapters.Mail;
using Microsoft.Extensions.Configuration;

namespace Beacon.SampleProject.Services;

public class BeaconMailSender(IConfiguration configuration) : IEmailAdapter
{
    public async Task SendEmailAsync(string to, string subject, string body, QueryResultFile? file)
    {
        var section = configuration.GetSection("Beacon:Smtp");

        var host = section["Host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("Beacon:Smtp:Host is not configured.");
        }

        var fromAddress = section["FromAddress"];
        if (string.IsNullOrWhiteSpace(fromAddress))
        {
            throw new InvalidOperationException("Beacon:Smtp:FromAddress is not configured.");
        }

        var username = section["Username"];
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("Beacon:Smtp:Username is not configured.");
        }

        var password = section["Password"];
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Beacon:Smtp:Password is not configured.");
        }

        var port = section.GetValue<int?>("Port") ?? 465;
        var enableSsl = section.GetValue<bool?>("EnableSsl") ?? true;

        using var smtpClient = new SmtpClient(host)
        {
            Port = port,
            EnableSsl = enableSsl,
            Credentials = new NetworkCredential(username, password)
        };

        var mailMessage = new MailMessage
        {
            From = new MailAddress(fromAddress),
            Subject = subject,
            Body = body,
            IsBodyHtml = true,
        };
        mailMessage.To.Add(to);

        if (file != null)
        {
            var attachment = new Attachment(new MemoryStream(file.Data), file.Name, file.ContentType);
            mailMessage.Attachments.Add(attachment);
        }

        await smtpClient.SendMailAsync(mailMessage);
    }
}
