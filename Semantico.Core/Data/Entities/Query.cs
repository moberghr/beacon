using Semantico.Core.Abstractions;
using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

public class Query : ArchivableBaseEntity, IMultiStepWorkflow
{
    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    /// <summary>
    /// Final query to execute against the in-memory SQLite database with all step results loaded
    /// Uses @result1, @result2, etc. to reference previous step results
    /// </summary>
    public string? FinalQuery { get; set; }

    public List<Subscription> Subscriptions { get; set; } = new();

    public List<QueryStep> Steps { get; set; } = new();

    /// <summary>
    /// Computed properties for backward compatibility and query analysis
    /// </summary>
    public bool IsMultiStep => Steps.Count > 1;
    
    public bool IsCrossProject => Steps.Select(s => s.ProjectId).Distinct().Count() > 1;
    
    public bool IsCrossDatabase => Steps.Select(s => s.Project.DatabaseEngineType).Distinct().Count() > 1;
    
    public List<int> ProjectIds => Steps.Select(s => s.ProjectId).Distinct().ToList();
    
    public List<DatabaseEngineType> DatabaseEngines => Steps.Select(s => s.Project.DatabaseEngineType).Distinct().ToList();
}
