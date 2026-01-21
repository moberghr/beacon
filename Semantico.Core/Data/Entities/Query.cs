using Semantico.Core.Abstractions;
using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

public class Query : ArchivableBaseEntity, IMultiStepWorkflow
{
    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    /// <summary>
    /// Folder ID for organization. Null means root level (no folder).
    /// </summary>
    public int? FolderId { get; set; }

    /// <summary>
    /// Final query to execute against the in-memory SQLite database with all step results loaded
    /// Uses @result1, @result2, etc. to reference previous step results
    /// </summary>
    public string? FinalQuery { get; set; }

    /// <summary>
    /// If this query was created by an AI Actor, the actor's ID.
    /// Null means user-created.
    /// </summary>
    public int? AiActorId { get; set; }

    /// <summary>
    /// Whether this query is locked from AI modifications.
    /// When true, AI Actors cannot modify this query's SQL.
    /// </summary>
    public bool IsLocked { get; set; }

    /// <summary>
    /// When the query was locked
    /// </summary>
    public DateTime? LockedAt { get; set; }

    /// <summary>
    /// User ID who locked the query
    /// </summary>
    public string? LockedByUserId { get; set; }

    public List<Subscription> Subscriptions { get; set; } = new();

    public List<QueryStep> Steps { get; set; } = new();

    // Navigation properties
    public QueryFolder? Folder { get; set; }

    public AiActor? AiActor { get; set; }

    /// <summary>
    /// Computed properties for backward compatibility and query analysis
    /// </summary>
    public bool IsMultiStep => Steps.Count > 1;

    public bool IsCrossDataSource => Steps.Select(s => s.DataSourceId).Distinct().Count() > 1;

    public bool IsCrossDatabase => Steps.Select(s => s.DataSource.DatabaseEngineType).Distinct().Count() > 1;

    public List<int> DataSourceIds => Steps.Select(s => s.DataSourceId).Distinct().ToList();

    public List<DatabaseEngineType> DatabaseEngines => Steps.Select(s => s.DataSource.DatabaseEngineType).Distinct().ToList();
}
