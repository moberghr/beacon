using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.Services;

/// <summary>
/// One-time cleanup that rewrites every encrypted secret through the current
/// <see cref="IEncryptionService.Encrypt"/> implementation (decrypt-then-encrypt).
/// Because the encryption format is versioned, legacy values still decrypt without
/// this routine — running it is an optional migration to the authenticated v2 format,
/// not a hard dependency.
/// </summary>
internal sealed class SecretReEncryptionService(
    IDbContextFactory<BeaconContext> contextFactory,
    IEncryptionService encryptionService)
{
    public async Task<int> ReEncryptAllAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var rewritten = 0;

        var dataSources = await context.DataSources
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);

        foreach (var dataSource in dataSources)
        {
            if (string.IsNullOrEmpty(dataSource.EncryptedConnectionData))
            {
                continue;
            }

            var plain = encryptionService.Decrypt(dataSource.EncryptedConnectionData);
            dataSource.EncryptedConnectionData = encryptionService.Encrypt(plain);
            rewritten++;
        }

        var repositories = await context.GitHubRepositories
            .ToListAsync(cancellationToken);

        foreach (var repository in repositories)
        {
            if (string.IsNullOrEmpty(repository.EncryptedAccessToken))
            {
                continue;
            }

            var plain = encryptionService.Decrypt(repository.EncryptedAccessToken);
            repository.EncryptedAccessToken = encryptionService.Encrypt(plain);
            rewritten++;
        }

        await context.SaveChangesAsync(cancellationToken);

        return rewritten;
    }
}
