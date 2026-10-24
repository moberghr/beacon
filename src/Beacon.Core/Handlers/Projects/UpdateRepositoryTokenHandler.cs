using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Services;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.Projects;

internal sealed class UpdateRepositoryTokenHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IEncryptionService encryptionService,
    IBeaconActorAccessor actorAccessor,
    ISecurityAuditRecorder securityAudit,
    ILogger<UpdateRepositoryTokenHandler> logger)
    : IRequestHandler<UpdateRepositoryTokenCommand>
{
    public async Task Handle(UpdateRepositoryTokenCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var repo = await context.GitHubRepositories
            .Where(x => x.Id == request.RepositoryId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Repository {request.RepositoryId} not found");

        var isSet = !string.IsNullOrWhiteSpace(request.AccessToken);
        repo.EncryptedAccessToken = isSet
            ? encryptionService.Encrypt(request.AccessToken!)
            : null;

        // Whether a token is set now, never the token.
        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.RepositoryTokenChanged)
                .On(SecurityAuditResources.Repository, repo.Id)
                .InProject(repo.ProjectId)
                .With(new SecurityAuditDetails().Add("tokenSet", isSet)),
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        logger.LogInformation(
            ProjectAccessEvents.RepositoryTokenChanged,
            "Repository access token {Change}: repository {RepositoryId}, project {ProjectId}, by user {ActorId}",
            isSet ? "set" : "cleared",
            repo.Id,
            repo.ProjectId,
            actor.LogId);
    }
}

/// <summary>Sets, replaces or (with no token) clears a repository's access token. For an Admin or a SecurityAdmin.</summary>
[ProjectScopeExempt("gated by the SecurityAdmin permission: a repository's access token is a credential decision", Policy = BeaconPermissions.SecurityAdminPolicy)]
public record UpdateRepositoryTokenCommand(int RepositoryId, string? AccessToken) : IRequest
{
    // The compiler-generated ToString prints every member; the secrets are printed redacted.
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"RepositoryId = {RepositoryId}, AccessToken = {RedactedPrint.Of(AccessToken)}");

        return true;
    }
}