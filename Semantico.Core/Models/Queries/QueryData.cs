using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.Queries;

public class QueryData
{
    public int? QueryId { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }
    
    public DateTime CreatedTime { get; set; }
        
    public int SubscriptionsCount { get; set; }

    public List<QueryStepData> Steps { get; set; } = new();

    /// <summary>
    /// Final query to execute against the in-memory SQLite database with all step results loaded
    /// Uses @result1, @result2, etc. to reference previous step results
    /// </summary>
    public string? FinalQuery { get; set; }

    /// <summary>
    /// Project ID where the final query should be executed (for database engine context)
    /// If null, defaults to the first step's project
    /// </summary>
    public int? FinalQueryProjectId { get; set; }

    /// <summary>
    /// Cross-project computed properties
    /// </summary>
    public bool IsMultiStep => Steps.Count > 1;
    
    public bool IsCrossProject => Steps.Select(s => s.ProjectId).Distinct().Count() > 1;
    
    public bool IsCrossDatabase => Steps.Select(s => s.DatabaseEngineType).Distinct().Count() > 1;
    
    public List<string> ProjectNames => Steps.Select(s => s.ProjectName).Distinct().ToList();
    
    public List<DatabaseEngineType> DatabaseEngines => Steps.Select(s => s.DatabaseEngineType).Distinct().ToList();

    /// <summary>
    /// Backward compatibility properties (map to first step)
    /// </summary>
    public string SqlValue
    {
        get => Steps.OrderBy(s => s.StepOrder).FirstOrDefault()?.SqlValue ?? "";
        set
        {
            EnsureSingleStep();
            Steps[0].SqlValue = value;
        }
    }
    
    public int ProjectId
    {
        get => Steps.OrderBy(s => s.StepOrder).FirstOrDefault()?.ProjectId ?? 0;
        set
        {
            EnsureSingleStep();
            Steps[0].ProjectId = value;
        }
    }
    
    public string ProjectName
    {
        get => Steps.OrderBy(s => s.StepOrder).FirstOrDefault()?.ProjectName ?? "";
        set
        {
            EnsureSingleStep();
            Steps[0].ProjectName = value;
        }
    }
    
    public List<QueryParameterData> Parameters
    {
        get => Steps.OrderBy(s => s.StepOrder).FirstOrDefault()?.Parameters.Select(ConvertToQueryParameterData).ToList() ?? new();
        set
        {
            EnsureSingleStep();
            Steps[0].Parameters = value.Select(ConvertToQueryStepParameterData).ToList();
        }
    }

    /// <summary>
    /// Ensure this QueryData has exactly one step for backward compatibility operations
    /// </summary>
    private void EnsureSingleStep()
    {
        if (Steps.Count == 0)
        {
            Steps.Add(new QueryStepData
            {
                StepOrder = 1,
                Name = "Step 1",
                SqlValue = "",
                ProjectId = 0,
                ProjectName = "",
                DatabaseEngineType = DatabaseEngineType.PostgreSQL,
                Parameters = new()
            });
        }
    }

    /// <summary>
    /// Convert QueryStepParameterData to QueryParameterData for backward compatibility
    /// </summary>
    private static QueryParameterData ConvertToQueryParameterData(QueryStepParameterData stepParam)
    {
        return new QueryParameterData
        {
            Name = stepParam.Name,
            Type = stepParam.Type,
            Description = stepParam.Description ?? "",
            Placeholder = stepParam.Placeholder ?? ""
        };
    }

    /// <summary>
    /// Convert QueryParameterData to QueryStepParameterData
    /// </summary>
    private static QueryStepParameterData ConvertToQueryStepParameterData(QueryParameterData queryParam)
    {
        return new QueryStepParameterData
        {
            Name = queryParam.Name,
            Type = queryParam.Type,
            Description = queryParam.Description,
            Placeholder = queryParam.Placeholder
        };
    }
}