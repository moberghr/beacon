using MessageCardModel;
using Refit;
using System.Text;

namespace Semantico.Api.Adapters.Teams;

public interface ITeamsAdapter
{
    Task SendTeamsNotificationAsync(MessageRequest messageRequest);
}

public class TeamsAdapter : ITeamsAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;

    public TeamsAdapter(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task SendTeamsNotificationAsync(MessageRequest messageRequest)
    {
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(messageRequest.Recipient);
        var teamsNotificationApi = RestService.For<ITeamsNotificationApi>(client);

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

        await teamsNotificationApi.SendTeamsNotificationAsync(content);
    }
}