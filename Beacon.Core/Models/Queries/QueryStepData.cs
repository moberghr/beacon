using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.Queries;

public class QueryStepData
{
    public int StepId { get; set; }

    public int StepOrder { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public string SqlValue { get; set; } = null!;

    /// <summary>
    /// Data source information for this step
    /// </summary>
    public int DataSourceId { get; set; }

    public string DataSourceName { get; set; } = null!;

    public DataSourceType DataSourceType { get; set; }

    /// <summary>
    /// Only applicable for Database type data sources
    /// </summary>
    public DatabaseEngineType? DatabaseEngineType { get; set; }

    public string DatabaseEngineDescription => DatabaseEngineType?.ToString() ?? DataSourceType.ToString();

    public List<QueryStepParameterData> Parameters { get; set; } = new();
}