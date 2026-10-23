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
/// changed. A keyed recipient hash of a definition the recipient does not hold cannot be recomputed (its plaintext is not
/// stored): while a previous key is configured it is counted as an unresolved key dependency and logged by id, until the
/// recipient's pending change is decided (an approval writes the approved hash under the current key) or the recipient is
/// saved back to its approved destination. Once a run reports no value left under a previous key, none unreadable and no
/// unresolved key dependency, the previous keys can be removed from the configuration. A value in the legacy v1 format
/// (unauthenticated AES-CBC) is rewritten in the current format whichever configured key decrypts it, and counted in
/// <see cref="StoredValueReEncryptionResult.LegacyFormat"/>; once a run finds none (and none unreadable),
/// <c>Beacon:Encryption:AllowLegacyCbc</c> can be turned off. The sample host runs it as a background job at every start.
/// </summary>
public interface IStoredValueReEncryptionService
{
    /// <summary>
    /// Re-encrypts every stored value only a previous key decrypts; throws after the run when a value could not be
    /// written.
    /// </summary>
    Task<StoredValueReEncryptionResult> ReEncryptAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Counts the stored values only a previous key decrypts (<see cref="StoredValueReEncryptionResult.UnderPreviousKeys"/>),
    /// those no configured key decrypts (<see cref="StoredValueReEncryptionResult.Unreadable"/>) and the keyed hashes that
    /// cannot be rewritten under the current key (<see cref="StoredValueReEncryptionResult.UnresolvedKeyDependencies"/>),
    /// changing nothing.
    /// </summary>
    Task<StoredValueReEncryptionResult> CountAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One run's counts: values found under a previous key, re-encrypted, skipped because they changed while the run was going
/// (a save already encrypted them with the current key), that no configured key decrypts, and that failed to be written.
/// </summary>
public sealed record StoredValueReEncryptionResult(int UnderPreviousKeys, int ReEncrypted, int SkippedAsChanged, int Unreadable, int Failed)
{
    /// <summary>
    /// Keyed recipient hashes (approved, or of a pending change request) that name a definition the recipient does not hold,
    /// so they cannot be rewritten under the current key and may still need a previous key; counted only while a previous
    /// key is configured. The previous keys are not removable while any remain.
    /// </summary>
    public int UnresolvedKeyDependencies { get; init; }

    /// <summary>
    /// Values found in the legacy v1 format (unauthenticated AES-CBC), under the current or a previous key; a run rewrites
    /// them in the current format (counted in <see cref="ReEncrypted"/> or <see cref="SkippedAsChanged"/> like the others).
    /// </summary>
    public int LegacyFormat { get; init; }

    /// <summary>True when nothing stored needs a previous key any more: the previous keys can be removed.</summary>
    public bool PreviousKeysRemovable => UnderPreviousKeys == 0 && Unreadable == 0 && Failed == 0 && UnresolvedKeyDependencies == 0;

    /// <summary>
    /// True when no value stored is in the legacy v1 format (and none is unreadable or failed):
    /// <c>Beacon:Encryption:AllowLegacyCbc</c> can be turned off.
    /// </summary>
    public bool LegacyFormatReadRemovable => LegacyFormat == 0 && Unreadable == 0 && Failed == 0;
}

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
        if (!result.PreviousKeysRemovable || !result.LegacyFormatReadRemovable)
        {
            logger.LogWarning(
                ProjectAccessEvents.StoredValuesReEncrypted,
                "Stored value re-encryption: {UnderPreviousKeys} under a previous key, {LegacyFormat} in the legacy format, {ReEncrypted} re-encrypted with the current key, {Skipped} changed meanwhile, {Unreadable} readable with no configured key, {Failed} failed, {Unresolved} keyed recipient hashes that cannot be rewritten under the current key",
                result.UnderPreviousKeys,
                result.LegacyFormat,
                result.ReEncrypted,
                result.SkippedAsChanged,
                result.Unreadable,
                result.Failed,
                result.UnresolvedKeyDependencies);
        }
        else
        {
            logger.LogInformation(
                ProjectAccessEvents.StoredValuesReEncrypted,
                "Stored value re-encryption: no value is under a previous key or in the legacy format; Beacon:PreviousEncryptionKeys can be removed and Beacon:Encryption:AllowLegacyCbc turned off");
        }

        if (result.UnresolvedKeyDependencies > 0)
        {
            logger.LogWarning(
                ProjectAccessEvents.StoredValuesReEncrypted,
                "{Unresolved} keyed recipient hashes name a definition their recipient does not hold and may still need a previous key; keep {Setting} until none remain. Decide each recipient's pending change (an approval writes its approved hash under the current key), or save the recipient back to its approved destination, then run again",
                result.UnresolvedKeyDependencies,
                ServiceConfiguration.PreviousEncryptionKeysKey);
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
    // unchanged. A keyed hash of a definition the recipient does not hold (an approved destination it has moved away from,
    // a request of a change it no longer holds or of a recipient that is gone) cannot be recomputed, as its plaintext is
    // not stored, nor can a hash of a recipient whose destination cannot be read; nothing tells which key wrote it, so while
    // a previous key is configured each counts as an unresolved key dependency and the previous keys are not reported
    // removable.
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
            .ToDictionary(x => x.Id);

        foreach (var (id, approved, canonical) in canonicals.Values)
        {
            if (!IsKeyed(approved))
            {
                continue;
            }

            if (canonical == null || !hasher.Matches(approved, canonical))
            {
                run.Unresolved(hasher.HasPreviousKeys, "recipient approval", id, id);
                continue;
            }

            if (hasher.IsCurrent(approved, canonical))
            {
                continue;
            }

            await run.RewriteAsync("recipient approval", id, () => context.Recipients
                .IgnoreQueryFilters()
                .Where(x => x.Id == id)
                .Where(x => x.ApprovedDestinationHash == approved)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.ApprovedDestinationHash, hasher.Hash(canonical)), cancellationToken), cancellationToken);
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
            if (!IsKeyed(request.DefinitionHash))
            {
                continue;
            }

            var canonical = canonicals.TryGetValue(request.ResourceId, out var recipient) ? recipient.Canonical : null;
            if (canonical == null || !hasher.Matches(request.DefinitionHash, canonical))
            {
                run.Unresolved(hasher.HasPreviousKeys, "recipient change request", request.Id, request.ResourceId);
                continue;
            }

            if (hasher.IsCurrent(request.DefinitionHash, canonical))
            {
                continue;
            }

            await run.RewriteAsync("recipient change request", request.Id, () => context.ChangeApprovalRequests
                .Where(x => x.Id == request.Id)
                .Where(x => x.DefinitionHash == request.DefinitionHash)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.DefinitionHash, hasher.Hash(canonical)), cancellationToken), cancellationToken);
        }
    }

    // A keyed hash, as opposed to the marker of a resource with nothing approved yet.
    private static bool IsKeyed(string? hash)
    {
        return hash != null && hash.StartsWith(RecipientDestinationHasher.Prefix, StringComparison.Ordinal);
    }

    // The counts of one run, and the re-encryption of one value (or, without write, only its count).
    private sealed class Run(EncryptionService encryption, ILogger logger, bool write)
    {
        public EncryptionService Encryption => encryption;

        private int _underPreviousKeys;
        private int _legacyFormat;
        private int _reEncrypted;
        private int _skipped;
        private int _unreadable;
        private int _failed;
        private int _unresolved;

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
            var match = encryption.Identify(cipherText, out var legacyFormat);
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

            if (match != EncryptionService.KeyMatch.Previous && !legacyFormat)
            {
                return;
            }

            if (legacyFormat)
            {
                _legacyFormat++;
            }

            await RewriteAsync(
                kind,
                id,
                () => writeIfUnchanged(prefix + encryption.Encrypt(encryption.Decrypt(cipherText))),
                cancellationToken,
                underPreviousKey: match == EncryptionService.KeyMatch.Previous);
        }

        // A value found under a previous key (a cipher text, or a keyed hash) or in the legacy format is counted and, with
        // write, rewritten under the current key in the current format by writeIfUnchanged (a conditional update that
        // answers the rows it wrote).
        public async Task RewriteAsync(
            string kind,
            int id,
            Func<Task<int>> writeIfUnchanged,
            CancellationToken cancellationToken,
            bool underPreviousKey = true)
        {
            if (underPreviousKey)
            {
                _underPreviousKeys++;
            }

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

        // A keyed hash that cannot be rewritten under the current key: while a previous key is configured it may depend on
        // one, so it is counted and, with write, logged by id with how to resolve it.
        public void Unresolved(bool previousKeysConfigured, string kind, int id, int recipientId)
        {
            if (!previousKeysConfigured)
            {
                return;
            }

            _unresolved++;
            if (!write)
            {
                return;
            }

            logger.LogWarning(
                ProjectAccessEvents.StoredValuesReEncrypted,
                "The keyed hash of {Kind} {Id} names a definition recipient {RecipientId} does not hold, so it cannot be rewritten under the current key and may still need a previous key",
                kind,
                id,
                recipientId);
        }

        public StoredValueReEncryptionResult Result()
        {
            return new StoredValueReEncryptionResult(_underPreviousKeys, _reEncrypted, _skipped, _unreadable, _failed)
            {
                UnresolvedKeyDependencies = _unresolved,
                LegacyFormat = _legacyFormat
            };
        }
    }
}
