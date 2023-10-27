namespace Semantico.Api.Adapters.Mail;

public interface IMailAdapter
{
    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult);
}