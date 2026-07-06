using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;

namespace Beacon.AI.Services.Mcp;

public record RoutingResult
{
    public List<RoutedSource> Sources { get; init; } = [];
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
