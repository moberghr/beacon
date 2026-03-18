using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Services;

namespace Semantico.Core.Handlers.Projects;

internal sealed class UpdateRepositoryTokenHandler(SemanticoContext context, IEncryptionService encryptionService)
    : IRequestHandler<UpdateRepositoryTokenCommand>
{
    public async Task Handle(UpdateRepositoryTokenCommand request, CancellationToken cancellationToken)
    {
        var repo = await context.GitHubRepositories
            .FirstOrDefaultAsync(r => r.Id == request.RepositoryId, cancellationToken)
            ?? throw new InvalidOperationException($"Repository {request.RepositoryId} not found");

        repo.EncryptedAccessToken = !string.IsNullOrWhiteSpace(request.AccessToken)
            ? encryptionService.Encrypt(request.AccessToken)
            : null;

        await context.SaveChangesAsync(cancellationToken);
    }
}

public record UpdateRepositoryTokenCommand(int RepositoryId, string? AccessToken) : IRequest;