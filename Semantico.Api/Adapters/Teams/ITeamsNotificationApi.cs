using Refit;

namespace Semantico.Api.Adapters.Teams;

public interface ITeamsNotificationApi
{
    [Post("")]
    Task SendTeamsNotificationAsync([Body] StringContent message);
}