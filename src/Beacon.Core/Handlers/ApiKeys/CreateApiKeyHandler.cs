using MediatR;
using Microsoft.AspNetCore.Http;
using Beacon.Core.Authorization;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;

namespace Beacon.Core.Handlers.ApiKeys;

internal sealed class CreateApiKeyHandler(
    IApiKeyService apiKeyService,
    IHttpContextAccessor httpContextAccessor,
    IUserManagementService userManagementService,
    IBeaconAuthorizationProvider authorizationProvider)
    : IRequestHandler<CreateApiKeyCommand, CreateApiKeyResult>
{
    public async Task<CreateApiKeyResult> Handle(
        CreateApiKeyCommand request,
        CancellationToken cancellationToken)
    {
        // Keys are issued from a signed-in session only (a key issuing keys could outlive the key that created it), to
        // the user that session belongs to, so the audit trail and scope enforcement (§1.4) correlate keys to a user.
        var user = await ApiKeyManagementCaller.ResolveAsync(httpContextAccessor, userManagementService, cancellationToken);
        if (!user.IsEnabled)
        {
            throw new UnauthorizedAccessException("A disabled user cannot create API keys.");
        }

        // A key never does more than its owner may: Execute (writes, SQL, outbound calls, the LLM) needs write
        // permission — by the owner's own record, the rule every request with the key is checked against, and by the
        // authorization provider — so a Viewer issues Read keys only.
        var scopes = ApiKeyGrants.ParseRequestedScopes(request.Scopes);
        if (scopes.Contains(BeaconScopes.Execute) && !await CanWriteAsync(user, cancellationToken))
        {
            throw new UnauthorizedAccessException("Only users with write permission can create API keys with the Execute scope.");
        }

        // Stamped with the generation read together with the checks above: when the owner is disabled, archived or
        // re-enabled before the key is stored, the key is refused even though that change could not revoke it.
        var (_, plainTextKey) = await apiKeyService.GenerateApiKeyAsync(
            userId: user.Id,
            name: request.Name,
            scopes: scopes,
            allowedProjectIds: request.AllowedProjectIds,
            expiresAt: request.ExpiresAt,
            ownerGeneration: user.ApiKeyGeneration,
            ct: cancellationToken);

        return new CreateApiKeyResult(plainTextKey);
    }

    private async Task<bool> CanWriteAsync(BeaconUserData user, CancellationToken cancellationToken)
    {
        return ApiKeyGrants.OwnerCanWrite(user.IsSuperAdmin, user.Roles.Select(x => x.Level))
            && await authorizationProvider.HasWritePermissionAsync(cancellationToken);
    }
}

/// <summary>
/// Issues an API key for the signed-in user. Scopes: <c>Read</c> and/or <c>Execute</c> (Execute needs write
/// permission). The expiry defaults to 90 days from now and may not exceed <c>Beacon:ApiKeys:MaxLifetimeDays</c>
/// (365 by default).
/// </summary>
public record CreateApiKeyCommand(
    string Name,
    string[] Scopes,
    int[]? AllowedProjectIds,
    DateTime? ExpiresAt) : IRequest<CreateApiKeyResult>;

public record CreateApiKeyResult(string PlainTextKey);
