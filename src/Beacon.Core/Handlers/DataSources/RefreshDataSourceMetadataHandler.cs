using Beacon.Core.Configuration;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Metadata;
using MediatR;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class RefreshDataSourceMetadataHandler(
    ForcedMetadataRefresh refresh,
    ForcedMetadataRefreshCooldown cooldown,
    IOptionsMonitor<MetadataRefreshOptions> refreshOptions,
    IBeaconPermissionAccessor permissionAccessor,
    MetadataSampleView sampleView,
    ISecurityAuditRecorder securityAudit)
    : IRequestHandler<RefreshDataSourceMetadataCommand, DatabaseMetadataSnapshot>
{
    public async Task<DatabaseMetadataSnapshot> Handle(RefreshDataSourceMetadataCommand request, CancellationToken cancellationToken)
    {
        if (request.DataSourceId <= 0)
        {
            throw new InvalidOperationException("Data source id must be positive.");
        }

        // One forced refresh per data source per cooldown: a refresh scans the live database. An Admin or a DataSourceAdmin
        // is not held to the cooldown, but still waits for a refresh that is running. Only a refresh that succeeded starts
        // the cooldown, so a failed one can be retried at once.
        var cooldownMinutes = refreshOptions.CurrentValue.ForcedRefreshCooldownMinutes;
        var ignoreCooldown = (await permissionAccessor.GetCurrentAsync(cancellationToken)).Holds(BeaconPermissions.DataSourceAdmin);
        var refusal = cooldown.TryStart(request.DataSourceId, cooldownMinutes, ignoreCooldown);
        if (refusal != null)
        {
            throw new BeaconRateLimitedException(refusal);
        }

        DatabaseMetadataSnapshot snapshot;
        var succeeded = false;
        try
        {
            snapshot = await refresh.RunAsync(request.DataSourceId, cancellationToken);
            succeeded = true;
        }
        finally
        {
            cooldown.Finish(request.DataSourceId, cooldownMinutes, succeeded);
        }

        // A forced refresh scans the live database on a caller's request: on record, with whether it skipped the cooldown.
        await securityAudit.RecordAsync(
            new SecurityAuditEntry(SecurityAuditActions.MetadataRefreshed)
                .On(SecurityAuditResources.DataSource, request.DataSourceId)
                .With(new SecurityAuditDetails()
                    .Add("forced", true)
                    .Add("cooldownExempt", ignoreCooldown)),
            cancellationToken);

        // The sample values the refresh collected go to an Admin only, as on a read of the stored catalog.
        return await sampleView.ApplyAsync(snapshot, cancellationToken);
    }
}

/// <summary>
/// Re-reads a data source's metadata from the database now, in the request (a long scan holds the request open; the SPA
/// starts a background refresh instead, <see cref="StartDataSourceMetadataRefreshCommand"/>). Curated descriptions are
/// kept; a second forced refresh of the same data source within
/// <see cref="MetadataRefreshOptions.ForcedRefreshCooldownMinutes"/> of one that succeeded
/// (except by an Admin or a DataSourceAdmin), or while one is running, answers 429. The refreshed catalog carries sample
/// values for an Admin only.
/// </summary>
public record RefreshDataSourceMetadataCommand(int DataSourceId) : IRequest<DatabaseMetadataSnapshot>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        ProjectCheck.SharedSource(ProjectResource.DataSource(DataSourceId), ProjectAction.Manage)
    ];
}
