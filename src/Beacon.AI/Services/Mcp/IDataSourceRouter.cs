using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;

namespace Beacon.AI.Services.Mcp;

public record RoutingResult
{
    /// <summary>Most data sources one <c>ask</c> joins across.</summary>
    public const int MaxSources = 4;

    public List<RoutedSource> Sources { get; init; } = [];

    /// <summary>
    /// The project's own data sources the model named beyond <see cref="MaxSources"/> (each counted once); they are not in
    /// <see cref="Sources"/>. Above zero, the question spans more sources than one <c>ask</c> joins.
    /// </summary>
    public int DroppedSourceCount { get; init; }
}

public record RoutedSource
{
    public int DataSourceId { get; init; }
    public string DataSourceName { get; init; } = "";
    public string Reason { get; init; } = "";
}

internal record RoutingResponse
{
    public List<RoutingSourceEntry>? Sources { get; init; }
}

internal record RoutingSourceEntry
{
    public int DatasourceId { get; init; }
    public string? DatasourceName { get; init; }
    public string? Reason { get; init; }
}

public interface IDataSourceRouter
{
    Task<RoutingResult> RouteAsync(
        ILlmProvider llmProvider,
        List<DataSourceKnowledge> dataSources,
        string question,
        CancellationToken ct);
}
