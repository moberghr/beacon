namespace Semantico.Api.Adapters;

public interface INotificationAdapter
{
    public Task SendNotificationAsync(int subscriptionId, RecipientQueryResult recipientQueryResult);
}
