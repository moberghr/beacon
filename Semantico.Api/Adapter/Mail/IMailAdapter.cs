namespace Semantico.Api.Adapter.Mail;

public interface IMailAdapter
{
    Task SendMailAsync(SendEmailRequest sendEmailRequest);
}

public class SendEmailRequest
{
    public required string To { get; init; }

    public required string Subject { get; init; }

    public required string Body { get; init; }
}