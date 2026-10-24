using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services.Metadata;
using MediatR;
using Microsoft.Extensions.Options;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class StartDataSourceMetadataRefreshHandler(
    IBackgroundTaskService backgroundTasks,
    ForcedMetadataRefreshCooldown cooldown,
    IOptionsMonitor<MetadataRefreshOptions> refreshOptions,
    IBeaconPermissionAccessor permissionAccessor,
    ISecurityAuditRecorder securityAudit)
    : IRequestHandler<StartDataSourceMetadataRefreshCommand, BackgroundTaskTicket>
{
    public async Task<BackgroundTaskTicket> Handle(StartDataSourceMetadataRefreshCommand request, CancellationToken cancellationToken)
    {
        if (request.DataSourceId <= 0)
        {
            throw new InvalidOperationException("Data source id must be positive.");
        }

        // The same cooldown as the refresh in the request: an Admin or a DataSourceAdmin is not held to it. A refresh of the
        // source running in this process, or queued or running anywhere, refuses the start.
        var cooldownMinutes = refreshOptions.CurrentValue.ForcedRefreshCooldownMinutes;
        var ignoreCooldown = (await permissionAccessor.GetCurrentAsync(cancellationToken)).Holds(BeaconPermissions.DataSourceAdmin);
        var refusal = cooldown.Refusal(request.DataSourceId, cooldownMinutes, ignoreCooldown);
        if (refusal != null)
        {
            throw new BeaconRateLimitedException(refusal);
        }

        var ticket = await backgroundTasks.StartAsync(BackgroundTaskKind.MetadataRefresh, request.DataSourceId, cancellationToken)
            ?? throw new BeaconRateLimitedException(ForcedMetadataRefreshCooldown.AlreadyRunningMessage);

        // A forced refresh scans the live database on a caller's request: on record when it is accepted, with the task.
        await securityAudit.RecordAsync(
            new SecurityAuditEntry(SecurityAuditActions.MetadataRefreshed)
                .On(SecurityAuditResources.DataSource, request.DataSourceId)
                .With(new SecurityAuditDetails()
                    .Add("forced", true)
                    .Add("cooldownExempt", ignoreCooldown)
                    .Add("taskId", ticket.TaskId)),
            cancellationToken);

        return ticket;
    }
}

/// <summary>
/// Starts a forced re-read of a data source's metadata as a background task and answers at once with its ticket; its state
/// is read with <see cref="GetDataSourceMetadataRefreshQuery"/>, and the user who started it gets <c>JobStatusChanged</c>
/// for its job. The same cooldown as <see cref="RefreshDataSourceMetadataCommand"/> applies (429), and a refresh of the source
/// that is queued or running refuses a second one (429).
/// </summary>
public record StartDataSourceMetadataRefreshCommand(int DataSourceId) : IRequest<BackgroundTaskTicket>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        ProjectCheck.SharedSource(ProjectResource.DataSource(DataSourceId), ProjectAction.Manage)
    ];
}
