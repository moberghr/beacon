using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class GetDataSourceMetadataHandler(
    IDatabaseMetadataService metadataService,
    ISharedSourceSettings sharedSourceSettings,
    IQueryGuardrailService guardrail)
    : IRequestHandler<GetDataSourceMetadataQuery, DatabaseMetadataSnapshot>
{
    public async Task<DatabaseMetadataSnapshot> Handle(GetDataSourceMetadataQuery request, CancellationToken cancellationToken)
    {
        if (request.DataSourceId <= 0)
        {
            throw new InvalidOperationException("Data source id must be positive.");
        }

        var snapshot = await metadataService.GetMetadataAsync(request.DataSourceId, cancellationToken);

        if (snapshot == null)
        {
            throw new InvalidOperationException($"Data source {request.DataSourceId} metadata not available.");
        }

        // Stored sample values are shown under the guardrails that apply now: the strictest of the live projects that
        // link the data source. The cached snapshot itself is left as it is.
        var settings = await sharedSourceSettings.GetDataSourceSettingsAsync(request.DataSourceId, cancellationToken);

        return snapshot with
        {
            Tables = snapshot.Tables
                .Select(x => x with { Columns = WithStoredSamples(x.Columns, settings) })
                .ToList()
        };
    }

    private List<ColumnMetadataDto> WithStoredSamples(IReadOnlyList<ColumnMetadataDto> columns, McpSettingsData settings)
    {
        return columns
            .Select(x => WithStoredSamples(x, settings))
            .ToList();
    }

    private ColumnMetadataDto WithStoredSamples(ColumnMetadataDto column, McpSettingsData settings)
    {
        var shown = StoredSampleScreen.Screen(guardrail, column.ColumnName, column.SampleValues, settings);

        return shown?.Count == column.SampleValues?.Count
            ? column
            : column with { SampleValues = shown, SampleValuesComplete = false };
    }
}

public record GetDataSourceMetadataQuery(int DataSourceId) : IRequest<DatabaseMetadataSnapshot>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.DataSource(DataSourceId), ProjectAction.Read)
    ];
}
