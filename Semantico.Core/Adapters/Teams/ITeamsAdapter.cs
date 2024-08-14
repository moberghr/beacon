namespace Semantico.Core.Adapters.Teams;

internal interface ITeamsAdapter
{
    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult);

}
