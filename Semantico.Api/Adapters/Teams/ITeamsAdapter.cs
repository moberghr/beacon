namespace Semantico.Api.Adapters.Teams;

public interface ITeamsAdapter
{
    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult);

}
