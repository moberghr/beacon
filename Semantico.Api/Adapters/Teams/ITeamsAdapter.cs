using MessageCardModel;
using System.Text;
using Semantico.Api.Worker;

namespace Semantico.Api.Adapters.Teams;

public interface ITeamsAdapter
{
    Task SendTeamsNotificationAsync(MessageRequest message, string webhookUrl);
}

public class TeamsAdapter : ITeamsAdapter
{
    private readonly ITeamsNotificationApi _teamsNotificationApi;

    public TeamsAdapter(ITeamsNotificationApi teamsNotificationApi)
    {
        _teamsNotificationApi = teamsNotificationApi;
    }

    public async Task SendTeamsNotificationAsync(MessageRequest message, string webhookUrl)
    {

        var card = new MessageCard
        {
            Title = message.ProjectName,
            Text = $"Query executed successfuly with total records of: {message.TotalRecords}",
            Sections = new[]
            {
                new Section
                {
                    Title = "Sql Query",
                    Text = message.SqlQuery
                },
                new Section
                {
                    Title = "First 10 records",
                    Text = message.QueryResults
                }
            }
        };

        var jsonPayload = card.ToJson();

        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        await _teamsNotificationApi.SendTeamsNotificationAsync(webhookUrl, content);
    }
}