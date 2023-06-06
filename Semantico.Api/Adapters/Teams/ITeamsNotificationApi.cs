using Refit;

namespace Semantico.Api.Adapters.Teams;

public interface ITeamsNotificationApi
{
    [Post("/{webhookUrl}")]
    Task SendTeamsNotificationAsync(string webhookUrl, [Body] StringContent message);
}