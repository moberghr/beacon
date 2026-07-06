using MediatR;
using Beacon.Core.Authorization;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;

namespace Beacon.Core.Handlers.ApiKeys;

internal sealed class CreateApiKeyHandler(
    IApiKeyService apiKeyService,
    IBeaconUserContext userContext,
    IUserManagementService userManagementService)
    : IRequestHandler<CreateApiKeyCommand, CreateApiKeyResult>
{
    public async Task<CreateApiKeyResult> Handle(
        CreateApiKeyCommand request,
        CancellationToken cancellationToken)
    {
        // API keys must be bound to the authenticated user who minted them so the
        // audit trail and scope enforcement (§1.4) can correlate keys back to a user.
        var externalId = userContext.UserId
            ?? throw new InvalidOperationException("Cannot create API key without an authenticated user.");

        var user = await userManagementService.GetUserByExternalIdAsync(externalId, cancellationToken)
            ?? throw new InvalidOperationException($"Authenticated user '{externalId}' was not found.");

        var (credential, plainTextKey) = await apiKeyService.GenerateApiKeyAsync(
            userId: user.Id,
            name: request.Name,
            scopes: request.Scopes,
            allowedProjectIds: request.AllowedProjectIds,
            expiresAt: request.ExpiresAt,
            ct: cancellationToken);

        return new CreateApiKeyResult(plainTextKey);
    }
}

public record CreateApiKeyCommand(
    string Name,
    string[] Scopes,
    int[]? AllowedProjectIds,
    DateTime? ExpiresAt) : IRequest<CreateApiKeyResult>;

public record CreateApiKeyResult(string PlainTextKey);
