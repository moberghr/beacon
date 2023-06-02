using Semantico.Api.Worker;

namespace Semantico.Api.Adapters.Mail;

public interface IMailAdapter
{
    Task SendMailAsync(MessageRequest messageRequest, string email);
}