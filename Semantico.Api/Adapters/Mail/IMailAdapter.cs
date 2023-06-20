namespace Semantico.Api.Adapters.Mail;

public interface IMailAdapter
{
    Task SendMailAsync(RecipientQueryResult recipientQueryResult);
}