using Semantico.Core.Data.Enums;

namespace Semantico.Core.Abstractions;

/// <summary>
/// Represents a workflow that executes multiple steps, potentially across different data sources and databases
/// </summary>
public interface IMultiStepWorkflow
{
    /// <summary>
    /// Whether this workflow has multiple execution steps
    /// </summary>
    bool IsMultiStep { get; }

    /// <summary>
    /// Whether this workflow executes across multiple data sources
    /// </summary>
    bool IsCrossDataSource { get; }

    /// <summary>
    /// Whether this workflow executes across multiple database engines
    /// </summary>
    bool IsCrossDatabase { get; }

    /// <summary>
    /// List of data source IDs involved in this workflow
    /// </summary>
    List<int> DataSourceIds { get; }

    /// <summary>
    /// List of database engine types involved in this workflow
    /// </summary>
    List<DatabaseEngineType> DatabaseEngines { get; }
}
