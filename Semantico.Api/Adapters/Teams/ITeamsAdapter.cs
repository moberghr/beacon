using MessageCardModel;
using System.Text;
using Semantico.Api.Worker;

namespace Semantico.Api.Adapters.Teams;

public interface ITeamsAdapter
{
    Task SendTeamsNotificationAsync(MessageRequest messageRequest);
}

public class TeamsAdapter : ITeamsAdapter
{
    private readonly ITeamsNotificationApi _teamsNotificationApi;

    public TeamsAdapter(ITeamsNotificationApi teamsNotificationApi)
    {
        _teamsNotificationApi = teamsNotificationApi;
    }

    public async Task SendTeamsNotificationAsync(MessageRequest messageRequest)
    {

        var card = new MessageCard
        {
            Title = messageRequest.ProjectName,
            Text = $"Query executed successfuly with total records of: {messageRequest.TotalRecords}",
            Sections = new[]
            {
                new Section
                {
                    Title = "Sql Query",
                    Text = messageRequest.SqlQuery
                },
                new Section
                {
                    Title = "First 10 records",
                    Text = messageRequest.QueryResults
                }
            }
        };

        var jsonPayload = card.ToJson();

        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        await _teamsNotificationApi.SendTeamsNotificationAsync(messageRequest.Recipient, content);
    }
}