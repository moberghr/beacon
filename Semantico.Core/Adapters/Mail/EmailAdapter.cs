using Semantico.Core.Data.Enums;

namespace Semantico.Core.Adapters.Mail;

internal class EmailAdapter : IAdapter
{
    private readonly IEmailAdapter _emailAdapter;

    public NotificationType NotificationType => NotificationType.Email;

    public EmailAdapter(IEmailAdapter emailAdapter)
    {
        _emailAdapter = emailAdapter;
    }

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult)
    {
        var to = recipientQueryResult.Recipient;
        var subject = $"{recipientQueryResult.QueryResult.ProjectName} - {recipientQueryResult.SubscriptionName}";

        var body = $"Sql Query: \n{recipientQueryResult.QueryResult.SqlQuery} \n" +
            $"Query executed successfuly with total records of: {recipientQueryResult.QueryResult.TotalRecords} \n" +
            $"Results: {recipientQueryResult.QueryResult.QueryResults}";

        await _emailAdapter.SendEmailAsync(to, subject, body);
    }

    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int lastNotificationResultCount)
    {
        throw new NotSupportedException();
    }
}