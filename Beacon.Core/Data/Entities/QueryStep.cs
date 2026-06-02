using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

public class QueryStep : BaseEntity
{
    public required int QueryId { get; set; }

    public Query Query { get; set; } = null!;

    /// <summary>
    /// The data source this step will execute against - KEY ARCHITECTURAL CHANGE
    /// Each step can target a different database/data source
    /// </summary>
    public required int DataSourceId { get; set; }

    public DataSource DataSource { get; set; } = null!;

    public required int StepOrder { get; set; }

    public required string SqlValue { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public List<QueryStepParameter> Parameters { get; set; } = new();
}