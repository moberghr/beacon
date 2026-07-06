using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Projects;

internal sealed class UpdateRepositoryTokenHandler(IDbContextFactory<BeaconContext> contextFactory, IEncryptionService encryptionService)
    : IRequestHandler<UpdateRepositoryTokenCommand>
{
    public async Task Handle(UpdateRepositoryTokenCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var repo = await context.GitHubRepositories
            .Where(x => x.Id == request.RepositoryId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Repository {request.RepositoryId} not found");

        repo.EncryptedAccessToken = !string.IsNullOrWhiteSpace(request.AccessToken)
            ? encryptionService.Encrypt(request.AccessToken)
            : null;

        await context.SaveChangesAsync(cancellationToken);
    }
}

public record UpdateRepositoryTokenCommand(int RepositoryId, string? AccessToken) : IRequest;