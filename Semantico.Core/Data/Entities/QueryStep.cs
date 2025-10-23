using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities;

public class QueryStep : BaseEntity
{
    public required int QueryId { get; set; }
    
    public Query Query { get; set; } = null!;
    
    /// <summary>
    /// The project this step will execute against - KEY ARCHITECTURAL CHANGE
    /// Each step can target a different database/project
    /// </summary>
    public required int ProjectId { get; set; }
    
    public Project Project { get; set; } = null!;
    
    public required int StepOrder { get; set; }
    
    public required string SqlValue { get; set; }
    
    public string? Name { get; set; }
    
    public string? Description { get; set; }
    
    public List<QueryStepParameter> Parameters { get; set; } = new();
}