namespace Semantico.Core.Models.Queries;

public class QueryData
{
    public int? QueryId { get; set; }

    public string Name { get; set; }

    public string? Description { get; set; }
    
    public DateTime CreatedTime { get; set; }

    public string SqlValue { get; set; }

    public int ProjectId { get; set; }
    
    public string ProjectName { get; set; }
        
    public int SubscriptionsCount { get; set; }

    public List<QueryParameterData> Parameters { get; set; } = new();
}