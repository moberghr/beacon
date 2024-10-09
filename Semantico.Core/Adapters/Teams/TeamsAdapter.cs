using MessageCardModel;
using Semantico.Core.Data.Enums;
using System.Text;

namespace Semantico.Core.Adapters.Teams;

internal class TeamsAdapter : IAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;

    public NotificationType NotificationType => NotificationType.Teams;

    public TeamsAdapter(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task SendNotificationAsync(RecipientQueryResult recipientQueryResult)
    {
        var client = _httpClientFactory.CreateClient();

        var card = new MessageCard
        {
            Title = $"{recipientQueryResult.QueryResult.ProjectName} - {recipientQueryResult.SubscriptionName}",
            Text = $"Query executed successfuly with total records of: {recipientQueryResult.QueryResult.TotalRecords}",
            Sections = new[]
            {
                new Section
                {
                    Title = "Sql Query",
                    Text = recipientQueryResult.QueryResult.SqlQuery
                },
                new Section
                {
                    Title = "First 10 records",
                    Text = recipientQueryResult.QueryResult.QueryResults
                }
            }
        };

        var jsonPayload = card.ToJson();
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        await client.PostAsync(recipientQueryResult.Recipient, content);
    }

    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int lastNotificationResultCount)
    {
        throw new NotSupportedException();
    }
}