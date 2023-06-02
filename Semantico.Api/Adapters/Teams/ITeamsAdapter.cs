using MessageCardModel;
using System.Text;
using Semantico.Api.Worker;

namespace Semantico.Api.Adapters.Teams;

public interface ITeamsAdapter
{
    Task SendTeamsNotificationAsync(MessageRequest message, string webHook);
}

public class TeamsService : ITeamsAdapter
{
    private readonly HttpClient _httpClient;

    public TeamsService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task SendTeamsNotificationAsync(MessageRequest message, string webHook)
    {
        var card = new MessageCard
        {
            Title = message.ProjectName,
            Text = $"Query executed successfuly with total records of: {message.TotalRecords}",
            Sections = new[]
            {
                new Section
                {
                    Title = "First 10 records",
                    Text = message.QueryResults
                }
            }
        };

        var jsonPayload = card.ToJson();

        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        await _httpClient.PostAsync(webHook, content);
    }
}