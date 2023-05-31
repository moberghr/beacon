namespace Semantico.Api.Adapter.Mail.SendGrid;

public class SendGridSettings
{
    public required string Apikey { get; init; }

    public required string SenderEmail { get; init; }

    public required string SenderName { get; init; }
}