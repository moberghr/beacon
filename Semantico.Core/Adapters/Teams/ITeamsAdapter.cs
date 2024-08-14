namespace Semantico.Core.Adapters.Teams;

public interface ITeamsAdapter
{
    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult);

}
