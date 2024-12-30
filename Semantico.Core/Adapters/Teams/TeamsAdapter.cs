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
                    Text = GenerateTableFromQueryResults(recipientQueryResult.QueryResult.TopRecords) //recipientQueryResult.QueryResult.QueryResults
                }
            }
        };

        var jsonPayload = card.ToJson();
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        await client.PostAsync(recipientQueryResult.RecipientDestination, content);
    }

    public Task SendNotificationAsync(RecipientQueryResult recipientQueryResult, int lastNotificationResultCount)
    {
        throw new NotSupportedException();
    }
    
    private string GenerateTableFromQueryResults(IEnumerable<object> queryResults)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<table>");
        sb.AppendLine("<thead>");
        sb.AppendLine("<tr>");
        foreach (var property in queryResults.First().GetType().GetProperties())
        {
            sb.AppendLine($"<th>{property.Name}</th>");
        }
        sb.AppendLine("</tr>");
        sb.AppendLine("</thead>");
        sb.AppendLine("<tbody>");
        foreach (var result in queryResults)
        {
            sb.AppendLine("<tr>");
            foreach (var property in result.GetType().GetProperties())
            {
                sb.AppendLine($"<td>{property.GetValue(result)}</td>");
            }
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody>");
        sb.AppendLine("</table>");
        return sb.ToString();
    }
}