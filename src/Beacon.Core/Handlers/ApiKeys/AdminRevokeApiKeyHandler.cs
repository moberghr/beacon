using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.ApiKeys;

/// <summary>
/// Revokes any user's API key, for an Admin or a UserAdmin in a signed-in session: the UserAdmin policy on the route, and
/// an Admin or a UserAdmin now (<see cref="ApiKeyManagementCaller.ResolveAdministratorAsync"/>). A UserAdmin who is not an
/// Admin does not revoke the keys of a user only an Admin may change (<see cref="ProtectedUsers"/>: an Admin, a super
/// admin, a functional role holder; 403), except their own. An unknown id is an <see cref="InvalidOperationException"/>;
/// revoking a revoked key keeps its first revocation time.
/// </summary>
internal sealed class AdminRevokeApiKeyHandler(
    IApiKeyService apiKeyService,
    IUserManagementService userManagementService,
    IBeaconPermissionAccessor permissionAccessor,
    IDbContextFactory<BeaconContext> contextFactory,
    IOptions<RoleMappingOptions> roleMapping,
    ILogger<AdminRevokeApiKeyHandler> logger,
    IHttpContextAccessor? httpContextAccessor = null)
    : IRequestHandler<AdminRevokeApiKeyCommand>
{
    public const string ProtectedOwnerMessage =
        "Only an Admin can revoke the API keys of an Admin, a super admin or a holder of a functional role.";

    public async Task Handle(
        AdminRevokeApiKeyCommand request,
        CancellationToken cancellationToken)
    {
        var admin = await ApiKeyManagementCaller.ResolveAdministratorAsync(
            httpContextAccessor,
            userManagementService,
            permissionAccessor,
            [BeaconPermissions.UserAdmin],
            cancellationToken);

        if (!ApiKeyManagementCaller.IsAdmin(admin, await permissionAccessor.GetCurrentAsync(cancellationToken)))
        {
            await DemandOwnerNotProtectedAsync(request.KeyId, admin.Id, cancellationToken);
        }

        await apiKeyService.RevokeApiKeyAsync(request.KeyId, cancellationToken);

        // Identifiers only: the acting administrator and the key, never key material (§1.3).
        logger.LogInformation("API key {ApiKeyId} revoked by administrator {UserId}", request.KeyId, admin.Id);
    }

    // A key whose owner is protected is an Admin's to revoke; a UserAdmin's own keys stay theirs.
    private async Task DemandOwnerNotProtectedAsync(int keyId, int callerId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ownerId = await context.ApiKeyCredentials
            .Where(x => x.Id == keyId)
            .Select(x => x.UserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (ownerId == null || ownerId == callerId)
        {
            return;
        }

        if (await ProtectedUsers.IsProtectedAsync(context, ownerId.Value, roleMapping.Value, cancellationToken))
        {
            // Identifiers only (§1.11).
            logger.LogInformation(
                ProjectAccessEvents.AccessRefused,
                "API key {ApiKeyId} revoke refused: its owner {OwnerId} is protected, by user {UserId}",
                keyId,
                ownerId,
                callerId);

            throw new BeaconForbiddenException(ProtectedOwnerMessage);
        }
    }
}

[ProjectScopeExempt("key administration, gated by the UserAdmin permission; no project data", Policy = BeaconPermissions.UserAdminPolicy)]
public record AdminRevokeApiKeyCommand(int KeyId) : IRequest;
