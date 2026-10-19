using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.ApiKeys;

internal sealed class RevokeApiKeyHandler(
    IApiKeyService apiKeyService,
    IDbContextFactory<BeaconContext> contextFactory,
    IUserManagementService userManagementService,
    IBeaconRequestPrincipal requestPrincipal)
    : IRequestHandler<RevokeApiKeyCommand>
{
    public async Task Handle(
        RevokeApiKeyCommand request,
        CancellationToken cancellationToken)
    {
        // A user may only revoke their own keys (§1.4) — confirm ownership before revoking
        // so one user cannot revoke another user's key by guessing its id.
        var user = await ApiKeyManagementCaller.ResolveAsync(requestPrincipal, userManagementService, cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var ownsKey = await context.ApiKeyCredentials
            .Where(x => x.Id == request.KeyId)
            .Where(x => x.UserId == user.Id)
            .AnyAsync(cancellationToken);

        if (!ownsKey)
        {
            throw new InvalidOperationException($"API key {request.KeyId} not found for the current user.");
        }

        await apiKeyService.RevokeApiKeyAsync(request.KeyId, cancellationToken);
    }
}

[ProjectScopeExempt("owner-scoped: the caller's own key")]
public record RevokeApiKeyCommand(int KeyId) : IRequest;
