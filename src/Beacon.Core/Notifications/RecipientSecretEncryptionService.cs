using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;

namespace Beacon.Core.Notifications;

/// <summary>
/// Encrypts recipient destinations and custom headers stored before they were encrypted at rest. Idempotent and safe to
/// run repeatedly or from several replicas: it only touches values that are still plaintext, archived recipients
/// included, and each write is conditional on the stored value being unchanged, so a concurrent edit is never
/// overwritten. A recipient that fails is logged and skipped, the others are still encrypted, and the run then throws so
/// the job shows as failed. A schema migration cannot do this because it has no access to <c>Beacon:EncryptionKey</c>.
/// Nothing runs it automatically: an operator runs it once (for example as a background job) after every node runs a
/// version that reads encrypted secrets, since an older version cannot read what it writes.
/// </summary>
public interface IRecipientSecretEncryptionService
{
    /// <summary>Encrypts every plaintext recipient secret; throws after the run if any recipient could not be encrypted.</summary>
    Task<RecipientSecretEncryptionResult> EncryptStoredSecretsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One run's counts: recipients with plaintext secrets found, encrypted, skipped because they changed while the run was
/// going (already encrypted by that save), and failed.
/// </summary>
public sealed record RecipientSecretEncryptionResult(int Candidates, int Encrypted, int SkippedAsChanged, int Failed);

internal sealed class RecipientSecretEncryptionService(
    IDbContextFactory<BeaconContext> contextFactory,
    RecipientSecretProtector protector,
    ILogger<RecipientSecretEncryptionService> logger) : IRecipientSecretEncryptionService
{
    public async Task<RecipientSecretEncryptionResult> EncryptStoredSecretsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var candidates = await context.Recipients
            .IgnoreQueryFilters()
            .Where(x => !x.Destination.StartsWith(RecipientSecretProtector.EncryptedPrefix)
                || (x.HeadersJson != null && x.HeadersJson != string.Empty && !x.HeadersJson.StartsWith(RecipientSecretProtector.EncryptedPrefix)))
            .Select(x =>
                new
                {
                    x.Id,
                    x.Destination,
                    x.HeadersJson,
                })
            .ToListAsync(cancellationToken);

        var encrypted = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                var destination = RecipientSecretProtector.IsProtected(candidate.Destination)
                    ? candidate.Destination
                    : protector.Protect(candidate.Destination);

                var headers = string.IsNullOrEmpty(candidate.HeadersJson) || RecipientSecretProtector.IsProtected(candidate.HeadersJson)
                    ? candidate.HeadersJson
                    : protector.Protect(candidate.HeadersJson);

                var originalDestination = candidate.Destination;
                var originalHeaders = candidate.HeadersJson;

                // Compare-and-swap: a recipient edited since it was read keeps the edit (already encrypted on save).
                var rows = await context.Recipients
                    .IgnoreQueryFilters()
                    .Where(x => x.Id == candidate.Id)
                    .Where(x => x.Destination == originalDestination)
                    .Where(x => x.HeadersJson == originalHeaders)
                    .ExecuteUpdateAsync(
                        x => x
                            .SetProperty(y => y.Destination, destination)
                            .SetProperty(y => y.HeadersJson, headers),
                        cancellationToken);

                if (rows > 0)
                {
                    encrypted++;
                }
                else
                {
                    skipped++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError(
                    "Could not encrypt the stored secrets of notification recipient {RecipientId} ({ExceptionTypes})",
                    candidate.Id,
                    NotificationHttpClient.TypeChain(ex));
            }
        }

        var result = new RecipientSecretEncryptionResult(candidates.Count, encrypted, skipped, failed);
        logger.LogInformation(
            "Recipient secret encryption: {Candidates} with plaintext secrets, {Encrypted} encrypted, {Skipped} changed meanwhile, {Failed} failed",
            result.Candidates,
            result.Encrypted,
            result.SkippedAsChanged,
            result.Failed);

        if (failed > 0)
        {
            throw new InvalidOperationException($"{failed} notification recipients could not be encrypted; see the log for their ids.");
        }

        return result;
    }
}
