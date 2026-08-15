using Beacon.Core.Services.Metadata;
using MediatR;

namespace Beacon.Core.Handlers.Metadata;

/// <summary>
/// Connectivity report for a data source's schema graph. Disconnected islands and isolated tables are
/// exactly where a user must declare relationships by hand — on a warehouse with no enforced foreign
/// keys that is most of the schema until inference or manual registration fills it in.
/// </summary>
internal sealed class GetSchemaHealthHandler(ISchemaGraphService schemaGraphService)
    : IRequestHandler<GetSchemaHealthQuery, GetSchemaHealthResult>
{
    public async Task<GetSchemaHealthResult> Handle(GetSchemaHealthQuery request, CancellationToken cancellationToken)
    {
        if (request.DataSourceId <= 0)
        {
            throw new InvalidOperationException("Data source id must be positive.");
        }

        var report = await schemaGraphService.GetHealthAsync(request.DataSourceId, cancellationToken);

        return new GetSchemaHealthResult
        {
            TableCount = report.TableCount,
            RelationshipCount = report.RelationshipCount,
            VerifiedRelationshipCount = report.VerifiedRelationshipCount,
            UnverifiedRelationshipCount = report.UnverifiedRelationshipCount,
            ComponentCount = report.ComponentCount,
            LargestComponentSize = report.LargestComponentSize,
            IsolatedTables = report.IsolatedTables,
            JunctionTables = report.JunctionTables
        };
    }
}

public record GetSchemaHealthQuery(int DataSourceId) : IRequest<GetSchemaHealthResult>;

public record GetSchemaHealthResult
{
    public int TableCount { get; init; }
    public int RelationshipCount { get; init; }
    public int VerifiedRelationshipCount { get; init; }
    public int UnverifiedRelationshipCount { get; init; }
    public int ComponentCount { get; init; }
    public int LargestComponentSize { get; init; }
    public IReadOnlyList<string> IsolatedTables { get; init; } = [];
    public IReadOnlyList<string> JunctionTables { get; init; } = [];
}
