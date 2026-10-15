using System.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.Projects;

internal sealed class UnlinkProjectDataSourceHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IBeaconActorAccessor actorAccessor,
    ISecurityAuditRecorder securityAudit,
    ILogger<UnlinkProjectDataSourceHandler> logger)
    : IRequestHandler<UnlinkProjectDataSourceCommand, UnlinkProjectDataSourceResult>
{
    public async Task<UnlinkProjectDataSourceResult> Handle(UnlinkProjectDataSourceCommand request, CancellationToken cancellationToken)
    {
        // An Admin now (live roles), whatever role claims the session was signed in with.
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        if (!scope.IsUnrestricted)
        {
            throw AccessRefusal.Of(logger, actor, "data source link", request.DataSourceId, "Only an Admin can unlink a data source from a project.");
        }

        try
        {
            return await UnlinkAsync(request, actor, cancellationToken);
        }
        catch (Exception ex) when (DbSerializationConflict.IsSerializationConflict(ex))
        {
            throw new BeaconConflictException("The data source's links changed concurrently; retry.");
        }
    }

    private async Task<UnlinkProjectDataSourceResult> UnlinkAsync(
        UnlinkProjectDataSourceCommand request,
        BeaconActor actor,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The other links are read and the link removed in one serializable transaction, so a concurrent unlink of the
        // source's other link cannot leave it unlinked and still open.
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var link = await context.ProjectDataSources
            .IgnoreQueryFilters()
            .Include(x => x.DataSource)
            .Where(x => x.ProjectId == request.ProjectId)
            .Where(x => x.DataSourceId == request.DataSourceId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Data source {request.DataSourceId} is not linked to project {request.ProjectId}.");

        if (link.DataSource.HostManagedKey != null)
        {
            throw new BeaconConflictException("A host's data source is linked by the host and cannot be unlinked.");
        }

        // Links to archived projects count: they keep the source linked, and never make it open.
        var stillLinked = await context.ProjectDataSources
            .Where(x => x.DataSourceId == request.DataSourceId)
            .Where(x => x.ProjectId != request.ProjectId)
            .AnyAsync(cancellationToken);

        // A source never becomes unlinked and open: once its last link goes, only an Admin reaches it.
        if (!stillLinked)
        {
            link.DataSource.OpenWhenUnlinked = false;
        }

        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.DataSourceUnlinked)
                .On(SecurityAuditResources.Project, request.ProjectId)
                .InProject(request.ProjectId)
                .With(new SecurityAuditDetails()
                    .Add("dataSourceId", request.DataSourceId)
                    .Add("leftUnlinked", !stillLinked)),
            cancellationToken);
        context.ProjectDataSources.Remove(link);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            ProjectAccessEvents.DataSourceUnlinked,
            "Data source unlinked: project {ProjectId}, data source {DataSourceId}, left unlinked {LeftUnlinked}, by user {ActorId}",
            request.ProjectId,
            request.DataSourceId,
            !stillLinked,
            actor.LogId);

        return new UnlinkProjectDataSourceResult(request.ProjectId, request.DataSourceId, !stillLinked);
    }
}

/// <summary>
/// Removes a data source's link to a project. Admin only. When it was the source's last link, the source is left
/// reachable by Admins only (it never becomes unlinked and open). 404 when the source is not linked to the project, 409
/// for a host's data source (the host links it) and when the links change concurrently (retry).
/// </summary>
[ProjectScopeExempt("admin-gated: unlinking a data source from a project is an Admin's", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record UnlinkProjectDataSourceCommand(int ProjectId, int DataSourceId) : IRequest<UnlinkProjectDataSourceResult>;

/// <summary><c>LeftUnlinked</c>: the source is linked to no project now, so only an Admin reaches it.</summary>
public record UnlinkProjectDataSourceResult(int ProjectId, int DataSourceId, bool LeftUnlinked);
