namespace Semantico.Core.Models.Queries;

public class QueryData
{
    public int? QueryId { get; set; }

    public string Name { get; set; }

    public string? Description { get; set; }
    
    public DateTime CreatedTime { get; set; }

    public required string SqlValue { get; set; }

    public required int ProjectId { get; set; }
        
    public required int SubscriptionsCount { get; set; }

    public List<QueryParameterData> Parameters { get; set; } = new();
}