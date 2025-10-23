using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.Queries;

public class QueryStepData
{
    public int StepId { get; set; }
    
    public int StepOrder { get; set; }
    
    public string Name { get; set; } = null!;
    
    public string? Description { get; set; }
    
    public string SqlValue { get; set; } = null!;
    
    /// <summary>
    /// Project information for this step
    /// </summary>
    public int ProjectId { get; set; }
    
    public string ProjectName { get; set; } = null!;
    
    public DatabaseEngineType DatabaseEngineType { get; set; }
    
    public string DatabaseEngineDescription => DatabaseEngineType.ToString();
    
    public List<QueryStepParameterData> Parameters { get; set; } = new();
}