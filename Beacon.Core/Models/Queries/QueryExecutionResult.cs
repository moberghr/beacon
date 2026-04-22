using Beacon.Core.Adapters;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.Queries;

public class QueryExecutionResult
{
    public List<QueryStepResult> StepResults { get; set; } = new();
    public QueryResult? FinalResult { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public double TotalExecutionTimeMs { get; set; }

    // Cross-data-source analysis
    public bool IsMultiStep { get; set; }
    public bool IsCrossDataSource { get; set; }
    public bool IsCrossDatabase { get; set; }
    public List<string> DataSourcesInvolved { get; set; } = new();
    public List<DatabaseEngineType> DatabaseEnginesUsed { get; set; } = new();
    public Dictionary<string, double> ExecutionTimeByDataSource { get; set; } = new();
}

public class QueryStepResult
{
    public int StepOrder { get; set; }
    public string StepName { get; set; } = null!;
    public string SqlQuery { get; set; } = null!;

    // Data source context for this step
    public string DataSourceName { get; set; } = null!;
    public string DatabaseEngine { get; set; } = null!;
    public DatabaseEngineType DatabaseEngineType { get; set; }
    
    public List<IDictionary<string, object?>> PreviewResults { get; set; } = new();
    public List<IDictionary<string, object?>> AllResults { get; set; } = new();
    public int TotalRows { get; set; }
    public double ExecutionTimeMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}