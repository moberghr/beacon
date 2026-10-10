using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;

namespace Beacon.Core.Handlers.ApiKeys;

/// <summary>
/// Revokes any user's API key, for an administrator (Admin role, enforced on the route) in a signed-in session. An
/// unknown id is an <see cref="InvalidOperationException"/>; revoking a revoked key keeps its first revocation time.
/// </summary>
internal sealed class AdminRevokeApiKeyHandler(
    IApiKeyService apiKeyService,
    IHttpContextAccessor httpContextAccessor,
    IUserManagementService userManagementService,
    ILogger<AdminRevokeApiKeyHandler> logger)
    : IRequestHandler<AdminRevokeApiKeyCommand>
{
    public async Task Handle(
        AdminRevokeApiKeyCommand request,
        CancellationToken cancellationToken)
    {
        var admin = await ApiKeyManagementCaller.ResolveAsync(httpContextAccessor, userManagementService, cancellationToken);

        await apiKeyService.RevokeApiKeyAsync(request.KeyId, cancellationToken);

        // Identifiers only: the acting administrator and the key, never key material (§1.3).
        logger.LogInformation("API key {ApiKeyId} revoked by administrator {UserId}", request.KeyId, admin.Id);
    }
}

public record AdminRevokeApiKeyCommand(int KeyId) : IRequest;
