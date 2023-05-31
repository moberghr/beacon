using System.Text;
using System.Text.Json;

namespace Semantico.Api.Adapters.Teams;

public interface ITeamsAdapter
{
    Task SendTeamsNotificationAsync(string message, string webHook);
}

public class TeamsService : ITeamsAdapter
{
    private readonly HttpClient _httpClient;

    public TeamsService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task SendTeamsNotificationAsync(string message, string webHook)
    {
        var msgCard = new SendTeamsRequest
        {
            Attachments = new[]
                {
                    new Attachments
                    {
                        Content = new Content
                        {
                            Body = new[]
                            {
                                new AdaptiveCardElement
                                {
                                    Text = message
                                }
                            }
                        }
                    }
                }
        };

        var jsonMessage = JsonSerializer.Serialize(msgCard);
        var content = new StringContent(jsonMessage, Encoding.UTF8, "application/json");

        await _httpClient.PostAsync(webHook, content);
    }
}