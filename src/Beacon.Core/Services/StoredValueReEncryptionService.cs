using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;

namespace Beacon.Core.Services;

/// <summary>
/// Moves stored values off the keys listed in <c>Beacon:PreviousEncryptionKeys</c>: every encrypted value at rest
/// (data-source connection data, repository access tokens, sensitive settings, recipient destinations and headers) that
/// only a previous key decrypts is re-encrypted with <c>Beacon:EncryptionKey</c>, and every keyed hash of where a governed
/// recipient delivers (its approved hash, its pending change request's) that matches the definition it holds only under a
/// previous key is recomputed under the current key. Idempotent and safe to run repeatedly or
/// from several replicas: each write is conditional on the stored value being unchanged, so a concurrent save wins (it is
/// already encrypted with the current key). Values no configured key decrypts are counted and logged by id, never
/// changed. Once a run reports no value left under a previous key, the previous keys can be removed from the
/// configuration. The sample host runs it as a background job at startup while a previous key is configured.
/// </summary>
public interface IStoredValueReEncryptionService
{
    /// <summary>
    /// Re-encrypts every stored value only a previous key decrypts; throws after the run when a value could not be
    /// written.
    /// </summary>
    Task<StoredValueReEncryptionResult> ReEncryptAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Counts the stored values only a previous key decrypts (<see cref="StoredValueReEncryptionResult.UnderPreviousKeys"/>)
    /// and those no configured key decrypts (<see cref="StoredValueReEncryptionResult.Unreadable"/>), changing nothing.
    /// </summary>
    Task<StoredValueReEncryptionResult> CountAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One run's counts: values found under a previous key, re-encrypted, skipped because they changed while the run was going
/// (a save already encrypted them with the current key), that no configured key decrypts, and that failed to be written.
/// </summary>
public sealed record StoredValueReEncryptionResult(int UnderPreviousKeys, int ReEncrypted, int SkippedAsChanged, int Unreadable, int Failed);

internal sealed class StoredValueReEncryptionService(
    IDbContextFactory<BeaconContext> contextFactory,
    IEncryptionService encryptionService,
    ILogger<StoredValueReEncryptionService> logger,
    RecipientDestinationHasher? hasher = null) : IStoredValueReEncryptionService
{
    public async Task<StoredValueReEncryptionResult> ReEncryptAsync(CancellationToken cancellationToken)
    {
        if (encryptionService is not EncryptionService encryption)
        {
            logger.LogWarning(
                ProjectAccessEvents.StoredValuesReEncrypted,
                "Stored values are not re-encrypted: the host replaced IEncryptionService, and only Beacon's own knows which key a value was written with");

            return new StoredValueReEncryptionResult(0, 0, 0, 0, 0);
        }

        var result = await ScanAsync(new Run(encryption, logger, write: true), cancellationToken);
        if (result.UnderPreviousKeys > 0 || result.Unreadable > 0 || result.Failed > 0)
        {
            logger.LogWarning(
                ProjectAccessEvents.StoredValuesReEncrypted,
                "Stored value re-encryption: {UnderPreviousKeys} under a previous key, {ReEncrypted} re-encrypted with the current key, {Skipped} changed meanwhile, {Unreadable} readable with no configured key, {Failed} failed",
                result.UnderPreviousKeys,
                result.ReEncrypted,
                result.SkippedAsChanged,
                result.Unreadable,
                result.Failed);
        }
        else
        {
            logger.LogInformation(
                ProjectAccessEvents.StoredValuesReEncrypted,
                "Stored value re-encryption: no value is under a previous key; Beacon:PreviousEncryptionKeys can be removed");
        }

        if (result.Failed > 0)
        {
            throw new InvalidOperationException($"{result.Failed} stored values could not be re-encrypted; see the log for their ids.");
        }

        return result;
    }

    public Task<StoredValueReEncryptionResult> CountAsync(CancellationToken cancellationToken)
    {
        return encryptionService is EncryptionService encryption
            ? ScanAsync(new Run(encryption, logger, write: false), cancellationToken)
            : Task.FromResult(new StoredValueReEncryptionResult(0, 0, 0, 0, 0));
    }

    // Every encrypted column, read with archived rows included; each value goes through the run.
    private async Task<StoredValueReEncryptionResult> ScanAsync(Run run, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSources = await context.DataSources
            .IgnoreQueryFilters()
            .Select(x =>
                new
                {
                    x.Id,
                    x.EncryptedConnectionData
                })
            .ToListAsync(cancellationToken);
        foreach (var row in dataSources)
        {
            await run.ReEncryptAsync("data source", row.Id, row.EncryptedConnectionData, string.Empty, value => context.DataSources
                .IgnoreQueryFilters()
                .Where(x => x.Id == row.Id)
                .Where(x => x.EncryptedConnectionData == row.EncryptedConnectionData)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.EncryptedConnectionData, value), cancellationToken), cancellationToken);
        }

        var repositories = await context.GitHubRepositories
            .Where(x => x.EncryptedAccessToken != null)
            .Select(x =>
                new
                {
                    x.Id,
                    x.EncryptedAccessToken
                })
            .ToListAsync(cancellationToken);
        foreach (var row in repositories)
        {
            await run.ReEncryptAsync("repository", row.Id, row.EncryptedAccessToken, string.Empty, value => context.GitHubRepositories
                .Where(x => x.Id == row.Id)
                .Where(x => x.EncryptedAccessToken == row.EncryptedAccessToken)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.EncryptedAccessToken, value), cancellationToken), cancellationToken);
        }

        var settings = await context.AppSettings
            .Where(x => x.IsSensitive)
            .Where(x => x.Value != null)
            .Select(x =>
                new
                {
                    x.Id,
                    x.Value
                })
            .ToListAsync(cancellationToken);
        foreach (var row in settings)
        {
            await run.ReEncryptAsync("setting", row.Id, row.Value, string.Empty, value => context.AppSettings
                .Where(x => x.Id == row.Id)
                .Where(x => x.Value == row.Value)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Value, value), cancellationToken), cancellationToken);
        }

        var recipients = await context.Recipients
            .IgnoreQueryFilters()
            .Where(x => x.Destination.StartsWith(RecipientSecretProtector.EncryptedPrefix)
                || (x.HeadersJson != null && x.HeadersJson.StartsWith(RecipientSecretProtector.EncryptedPrefix)))
            .Select(x =>
                new
                {
                    x.Id,
                    x.Destination,
                    x.HeadersJson
                })
            .ToListAsync(cancellationToken);
        foreach (var row in recipients)
        {
            await run.ReEncryptAsync("recipient destination", row.Id, row.Destination, RecipientSecretProtector.EncryptedPrefix, value => context.Recipients
                .IgnoreQueryFilters()
                .Where(x => x.Id == row.Id)
                .Where(x => x.Destination == row.Destination)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Destination, value), cancellationToken), cancellationToken);
            await run.ReEncryptAsync("recipient headers", row.Id, row.HeadersJson, RecipientSecretProtector.EncryptedPrefix, value => context.Recipients
                .IgnoreQueryFilters()
                .Where(x => x.Id == row.Id)
                .Where(x => x.HeadersJson == row.HeadersJson)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.HeadersJson, value), cancellationToken), cancellationToken);
        }

        if (hasher != null)
        {
            await RehashRecipientApprovalsAsync(context, run, hasher, new RecipientSecretProtector(run.Encryption), cancellationToken);
        }

        return run.Result();
    }

    // The keyed hashes of where governed recipients deliver (approved, and of their pending requests) that match the
    // recipient's definition only under a previous key: rewritten under the current key, each conditional on being
    // unchanged. A hash of a definition the recipient no longer holds cannot be recomputed (its plaintext is not stored);
    // it keeps matching under the previous key while that key is configured.
    private static async Task RehashRecipientApprovalsAsync(
        BeaconContext context,
        Run run,
        RecipientDestinationHasher hasher,
        RecipientSecretProtector protector,
        CancellationToken cancellationToken)
    {
        var governed = await context.Recipients
            .IgnoreQueryFilters()
            .Where(x => x.ApprovedDestinationHash != null)
            .Select(x =>
                new
                {
                    x.Id,
                    x.NotificationType,
                    x.Destination,
                    x.HeadersJson,
                    x.ApprovedDestinationHash
                })
            .ToListAsync(cancellationToken);
        var canonicals = governed
            .Select(x => (x.Id, x.ApprovedDestinationHash, Canonical: RecipientDestinationDefinition.CanonicalOfStored(protector, x.NotificationType, x.Destination, x.HeadersJson)))
            .Where(x => x.Canonical != null)
            .ToDictionary(x => x.Id);

        foreach (var (id, approved, canonical) in canonicals.Values)
        {
            if (!hasher.Matches(approved, canonical!) || hasher.IsCurrent(approved, canonical!))
            {
                continue;
            }

            await run.RewriteAsync("recipient approval", id, () => context.Recipients
                .IgnoreQueryFilters()
                .Where(x => x.Id == id)
                .Where(x => x.ApprovedDestinationHash == approved)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.ApprovedDestinationHash, hasher.Hash(canonical!)), cancellationToken), cancellationToken);
        }

        var requests = await context.ChangeApprovalRequests
            .Where(x => x.Kind == ChangeApprovalKind.RecipientDestination)
            .Where(x => x.Status == ApprovalStatus.Pending)
            .Select(x =>
                new
                {
                    x.Id,
                    x.ResourceId,
                    x.DefinitionHash
                })
            .ToListAsync(cancellationToken);
        foreach (var request in requests)
        {
            if (!canonicals.TryGetValue(request.ResourceId, out var recipient)
                || !hasher.Matches(request.DefinitionHash, recipient.Canonical!)
                || hasher.IsCurrent(request.DefinitionHash, recipient.Canonical!))
            {
                continue;
            }

            await run.RewriteAsync("recipient change request", request.Id, () => context.ChangeApprovalRequests
                .Where(x => x.Id == request.Id)
                .Where(x => x.DefinitionHash == request.DefinitionHash)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.DefinitionHash, hasher.Hash(recipient.Canonical!)), cancellationToken), cancellationToken);
        }
    }

    // The counts of one run, and the re-encryption of one value (or, without write, only its count).
    private sealed class Run(EncryptionService encryption, ILogger logger, bool write)
    {
        public EncryptionService Encryption => encryption;

        private int _underPreviousKeys;
        private int _reEncrypted;
        private int _skipped;
        private int _unreadable;
        private int _failed;

        // A stored value is the prefix (recipient secrets carry one) followed by the cipher text. A value only a previous key
        // decrypts is written again, encrypted with the current key, by writeIfUnchanged (a conditional update that answers
        // the rows it wrote).
        public async Task ReEncryptAsync(
            string kind,
            int id,
            string? stored,
            string prefix,
            Func<string, Task<int>> writeIfUnchanged,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(stored) || !stored.StartsWith(prefix, StringComparison.Ordinal))
            {
                return;
            }

            var cipherText = stored[prefix.Length..];
            var match = encryption.Identify(cipherText);
            if (match == EncryptionService.KeyMatch.None)
            {
                _unreadable++;
                if (!write)
                {
                    return;
                }

                logger.LogWarning(
                    ProjectAccessEvents.StoredValuesReEncrypted,
                    "Stored value of {Kind} {Id} cannot be decrypted with the current key or any previous key",
                    kind,
                    id);

                return;
            }

            if (match != EncryptionService.KeyMatch.Previous)
            {
                return;
            }

            await RewriteAsync(kind, id, () => writeIfUnchanged(prefix + encryption.Encrypt(encryption.Decrypt(cipherText))), cancellationToken);
        }

        // A value found under a previous key (a cipher text, or a keyed hash) is counted and, with write, rewritten under the
        // current key by writeIfUnchanged (a conditional update that answers the rows it wrote).
        public async Task RewriteAsync(string kind, int id, Func<Task<int>> writeIfUnchanged, CancellationToken cancellationToken)
        {
            _underPreviousKeys++;
            if (!write)
            {
                return;
            }

            try
            {
                var rows = await writeIfUnchanged();
                if (rows > 0)
                {
                    _reEncrypted++;
                }
                else
                {
                    _skipped++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _failed++;
                logger.LogError(
                    ProjectAccessEvents.StoredValuesReEncrypted,
                    "Stored value of {Kind} {Id} could not be re-encrypted ({ExceptionType})",
                    kind,
                    id,
                    ex.GetType().Name);
            }
        }

        public StoredValueReEncryptionResult Result()
        {
            return new StoredValueReEncryptionResult(_underPreviousKeys, _reEncrypted, _skipped, _unreadable, _failed);
        }
    }
}
