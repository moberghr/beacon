namespace Semantico.Api.Adapters.Jira;

public interface IJiraAdapter
{
    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult);

    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int lastNotificationResultCount);
}
