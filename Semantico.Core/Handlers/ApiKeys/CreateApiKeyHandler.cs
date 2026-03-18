using MediatR;
using Semantico.Core.Services.Security;

namespace Semantico.Core.Handlers.ApiKeys;

internal sealed class CreateApiKeyHandler(IApiKeyService apiKeyService)
    : IRequestHandler<CreateApiKeyCommand, CreateApiKeyResult>
{
    public async Task<CreateApiKeyResult> Handle(
        CreateApiKeyCommand request,
        CancellationToken cancellationToken)
    {
        var (credential, plainTextKey) = await apiKeyService.GenerateApiKeyAsync(
            userId: null, // TODO: pass current user ID
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
