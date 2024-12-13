namespace Semantico.Core.Adapters.Mail
{
    public interface IEmailAdapter
    {
        public Task SendEmailAsync(string to, string subject, string body);
    }
}
