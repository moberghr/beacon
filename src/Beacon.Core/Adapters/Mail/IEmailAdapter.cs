namespace Beacon.Core.Adapters.Mail;

public interface IEmailAdapter
{
    public Task SendEmailAsync(string to, string subject, string body, QueryResultFile? queryResultAttachmentFile);

    /// <summary>
    /// Sends the message and stops waiting when <paramref name="cancellationToken"/> is cancelled. The default forwards
    /// to the overload without a token and abandons the wait; an implementation that can cancel the send itself
    /// should override it, and Beacon calls this one.
    /// </summary>
    public Task SendEmailAsync(string to, string subject, string body, QueryResultFile? queryResultAttachmentFile, CancellationToken cancellationToken)
    {
        return SendEmailAsync(to, subject, body, queryResultAttachmentFile).WaitAsync(cancellationToken);
    }
}
