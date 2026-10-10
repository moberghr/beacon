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

namespace Beacon.Core.Handlers.Projects;

internal sealed class CreateProjectHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IEncryptionService encryptionService,
    IBeaconActorAccessor actorAccessor,
    IProjectAccessEvaluator projectAccess,
    IOptions<ProjectAuthorizationOptions> options,
    ILogger<CreateProjectHandler> logger)
    : IRequestHandler<CreateProjectCommand, CreateProjectResult>
{
    public async Task<CreateProjectResult> Handle(
        CreateProjectCommand request,
        CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        // A repository access token is an Admin's to set, whether here or on the repository's token route.
        var hasAccessToken = !string.IsNullOrWhiteSpace(request.AccessToken);
        if (hasAccessToken && !actor.IsAdmin)
        {
            throw AccessRefusal.Of(logger, actor, "repository access token", null, "Only an Admin can set a repository access token.");
        }

        var dataSourceIds = (request.DataSourceIds ?? [])
            .Distinct()
            .ToList();
        var scope = await projectAccess.GetScopeAsync(cancellationToken);

        try
        {
            var projectId = await CreateAsync(request, actor, scope, dataSourceIds, hasAccessToken, cancellationToken);

            return new CreateProjectResult(projectId);
        }
        catch (Exception ex) when (DbSerializationConflict.IsSerializationConflict(ex))
        {
            throw new BeaconConflictException("The data sources changed while the project was being created; retry.");
        }
    }

    private async Task<int> CreateAsync(
        CreateProjectCommand request,
        BeaconActor actor,
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
                logger);
        }

        var encryptedToken = hasAccessToken
            ? encryptionService.Encrypt(request.AccessToken!)
            : null;

        // Created open. Its creator, when an active Beacon user, is its ProjectAdmin, so they can add members before an
        // Admin restricts it. Each link records who made it.
        var project = new Project
        {
            Name = request.Name,
            Description = request.Description,
            RequiresMembership = false,
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
            Members = actor.BeaconUserId != null && actor.IsActiveUser
                ? [new ProjectMember { UserId = actor.BeaconUserId.Value, Role = ProjectRole.ProjectAdmin, AddedByUserId = actor.BeaconUserId }]
                : []
        };

        context.Projects.Add(project);
        await context.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return project.Id;
    }
}

/// <summary>
/// Creates an open project; its creator, when a Beacon user, becomes its ProjectAdmin. A non-Admin links only data
/// sources a live open project already holds; a host's data source cannot be linked. An <c>AccessToken</c> for its
/// repositories may be given by an Admin only. <c>DataSourceIds</c> and <c>RepositoryUrls</c> may be left out (none).
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

public record CreateProjectResult(int ProjectId);
