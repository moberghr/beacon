using System.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.Projects;

internal sealed class CreateProjectHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IEncryptionService encryptionService,
    IBeaconActorAccessor actorAccessor,
    IBeaconPermissionAccessor permissionAccessor,
    IProjectAccessEvaluator projectAccess,
    IProjectMembershipSource membershipSource,
    IOptions<ProjectAuthorizationOptions> options,
    ISecurityAuditRecorder securityAudit,
    ILogger<CreateProjectHandler> logger)
    : IRequestHandler<CreateProjectCommand, CreateProjectResult>
{
    public async Task<CreateProjectResult> Handle(
        CreateProjectCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        var permissions = await permissionAccessor.GetCurrentAsync(cancellationToken);

        // A repository access token is an Admin's or a SecurityAdmin's to set (live roles), whether here or on the
        // repository's token route.
        var hasAccessToken = !string.IsNullOrWhiteSpace(request.AccessToken);
        if (hasAccessToken && !permissions.Holds(BeaconPermissions.SecurityAdmin))
        {
            throw AccessRefusal.Of(logger, actor, "repository access token", null, "Only an Admin or a SecurityAdmin can set a repository access token.");
        }

        var dataSourceIds = (request.DataSourceIds ?? [])
            .Distinct()
            .ToList();
        var scope = await projectAccess.GetScopeAsync(cancellationToken);

        try
        {
            var project = await CreateAsync(request, actor, permissions, scope, dataSourceIds, hasAccessToken, cancellationToken);
            if (!project.RequiresMembership)
            {
                // Created open because its creator cannot be its member: no active Beacon user, or memberships kept elsewhere.
                logger.LogInformation(
                    ProjectAccessEvents.ProjectCreatedOpen,
                    "Project created open: project {ProjectId}, by {PrincipalKind} {ActorId}, membership managed by {MembershipManagement}",
                    project.Id,
                    actor.Kind,
                    actor.LogId,
                    ProjectMembershipRules.ManagementOf(membershipSource));
            }

            return new CreateProjectResult(project.Id, project.RequiresMembership);
        }
        catch (Exception ex) when (DbSerializationConflict.IsSerializationConflict(ex))
        {
            throw new BeaconConflictException("The data sources changed while the project was being created; retry.");
        }
    }

    private async Task<Project> CreateAsync(
        CreateProjectCommand request,
        BeaconActor actor,
        BeaconPermissionSet permissions,
        ProjectAccessScope scope,
        List<int> dataSourceIds,
        bool hasAccessToken,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Data sources are checked against the links read in the serializable transaction that writes the new ones.
        await using var transaction = dataSourceIds.Count > 0
            ? await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        if (transaction != null)
        {
            var candidates = await ProjectDataSourceLinks.ReadAsync(context, dataSourceIds, cancellationToken);
            ProjectDataSourceLinks.Check(
                scope,
                dataSourceIds,
                candidates,
                targetHostKey: null,
                _ => null,
                options.Value.RequireProjectMembership,
                actor,
                logger,
                permissions.Holds(BeaconPermissions.DataSourceAdmin));
        }

        var encryptedToken = hasAccessToken
            ? encryptionService.Encrypt(request.AccessToken!)
            : null;

        // Restricted, with its creator as its ProjectAdmin, when the creator is an active Beacon user and Beacon keeps the
        // memberships; otherwise open, since nobody could be its member. Each link records who made it.
        var creatorIsMember = actor.BeaconUserId != null
            && actor.IsActiveUser
            && ProjectMembershipRules.IsManagedByBeacon(membershipSource);
        var project = new Project
        {
            Name = request.Name,
            Description = request.Description,
            RequiresMembership = creatorIsMember,
            DataSources = dataSourceIds
                .Select(x => new ProjectDataSource { DataSourceId = x, LinkedByUserId = actor.BeaconUserId })
                .ToList(),
            Repositories = (request.RepositoryUrls ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => new GitHubRepository
                {
                    RepositoryUrl = x,
                    EncryptedAccessToken = encryptedToken
                })
                .ToList(),
            Members = creatorIsMember
                ? [new ProjectMember { UserId = actor.BeaconUserId!.Value, Role = ProjectRole.ProjectAdmin, AddedByUserId = actor.BeaconUserId }]
                : []
        };

        context.Projects.Add(project);
        await securityAudit.SaveWithEventsAsync(
            context,
            () =>
            [
                new SecurityAuditEntry(SecurityAuditActions.ProjectCreated)
                    .On(SecurityAuditResources.Project, project.Id)
                    .InProject(project.Id)
                    .With(new SecurityAuditDetails()
                        .Add("requiresMembership", project.RequiresMembership)
                        .Add("creatorIsMember", creatorIsMember)
                        .Add("dataSourceIds", dataSourceIds)
                        .Add("repositoryCount", project.Repositories.Count)
                        .Add("repositoryTokenSet", encryptedToken != null && project.Repositories.Count > 0))
            ],
            cancellationToken);
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return project;
    }
}

/// <summary>
/// Creates a project restricted to its members, with its creator as its ProjectAdmin, when the creator is an active Beacon
/// user and Beacon manages project membership; otherwise the project is created open. Anyone but an Admin or a
/// DataSourceAdmin links only data sources a live open project already holds, and a DataSourceAdmin no data source it
/// cannot read (403: its creator would read it through the new project); a host's data source cannot be linked. An
/// <c>AccessToken</c> for its repositories may be given by an Admin or a SecurityAdmin only. <c>DataSourceIds</c> and <c>RepositoryUrls</c> may be left out (none).
/// 409 when the data sources' links change concurrently (retry).
/// </summary>
public record CreateProjectCommand(
    string Name,
    string? Description,
    [property: ProjectScopeExempt("save-time: the link rules check each data source inside the handler's transaction (404 unreadable, 403 not linkable)")] List<int>? DataSourceIds,
    List<string>? RepositoryUrls,
    string? AccessToken = null) : IRequest<CreateProjectResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks => [];
}

/// <summary><paramref name="RequiresMembership"/>: whether the new project is restricted to its members.</summary>
public record CreateProjectResult(int ProjectId, bool RequiresMembership);
