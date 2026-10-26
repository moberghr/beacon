using Beacon.Core.Authorization;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class SetDataSourceEnabledHandler(DataSourceService dataSourceService, IBeaconActorAccessor actorAccessor)
    : IRequestHandler<SetDataSourceEnabledCommand>
{
    public async Task Handle(SetDataSourceEnabledCommand request, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        await dataSourceService.SetDataSourceEnabled(request.DataSourceId, request.Enabled, actor.BeaconUserId, cancellationToken);
    }
}

/// <summary>
/// Disables a data source, or enables it again (an Admin or a DataSourceAdmin), at once and without a second approval. A
/// disabled source makes no connection at all: every query, preview, subscription run, data-quality check, MCP tool, AI
/// path and metadata scan that reads it fails, and a subscription run is recorded as failed. Its stored catalog is not
/// served (a metadata read answers an empty one, as for a migration write target) and its cached metadata is evicted, so
/// no stored table, column or sampled value of it is shown while it is disabled. Host-managed sources are refused. Recorded as <c>data_sources.data_source_disabled</c> or <c>data_sources.data_source_enabled</c>, ids only.
/// </summary>
[ProjectScopeExempt("data-source administration, gated by the DataSourceAdmin permission", Policy = BeaconPermissions.DataSourceAdminPolicy)]
public record SetDataSourceEnabledCommand(int DataSourceId, bool Enabled) : IRequest;
