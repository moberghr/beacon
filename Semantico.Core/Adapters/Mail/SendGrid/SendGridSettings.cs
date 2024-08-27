namespace Semantico.Core.Adapters.Mail.SendGrid;

internal class SendGridSettings
{
    public required string Apikey { get; init; }

    public required string SenderEmail { get; init; }

    public required string SenderName { get; init; }
}