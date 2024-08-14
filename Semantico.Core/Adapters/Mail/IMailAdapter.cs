namespace Semantico.Core.Adapters.Mail;

public interface IMailAdapter
{
    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult);
}