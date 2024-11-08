namespace Semantico.Core.Models.Queries;

public class QueryData
{
    public int? QueryId { get; init; }

    public string Name { get; set; }

    public string? Description { get; set; }
    
    public DateTime CreatedTime { get; set; }

    public required string SqlValue { get; init; }

    public required int ProjectId { get; init; }
        
    public required int SubscriptionsCount { get; init; }

    public List<QueryParameterData> Parameters { get; init; } = new();
}