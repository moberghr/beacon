namespace Semantico.Core.Adapters.Mail;

internal interface IMailAdapter
{
    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult);
}