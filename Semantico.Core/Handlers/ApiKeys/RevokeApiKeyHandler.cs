using MediatR;
using Semantico.Core.Services.Security;

namespace Semantico.Core.Handlers.ApiKeys;

internal sealed class RevokeApiKeyHandler(IApiKeyService apiKeyService)
    : IRequestHandler<RevokeApiKeyCommand>
{
    public async Task Handle(
        RevokeApiKeyCommand request,
        CancellationToken cancellationToken)
    {
        await apiKeyService.RevokeApiKeyAsync(request.KeyId, cancellationToken);
    }
}

public record RevokeApiKeyCommand(int KeyId) : IRequest;
