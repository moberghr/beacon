using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Notifications;

namespace Beacon.Core.Services;

/// <summary>
/// Moves stored values off the keys listed in <c>Beacon:PreviousEncryptionKeys</c>: every encrypted value at rest
/// (data-source connection data, repository access tokens, sensitive settings, recipient destinations and headers, staged
/// LLM connection changes) that only a previous key decrypts is re-encrypted with <c>Beacon:EncryptionKey</c>, and every
/// keyed hash of where a governed recipient delivers or of a governed data source's connection definition (approved, or
/// of a pending change request) that matches the definition it holds only under a previous key is recomputed under the
/// current key. A run first expires the LLM connection changes that waited past their expiry (wiping what they staged); a
/// pending one whose recorded connection hash or shown fingerprints are not under the current key cannot be rewritten (its
/// reviewer's view is bound by its payload hash) and counts as an unresolved key dependency until it is decided, expires or
/// is submitted again. Idempotent and safe to run repeatedly or
/// from several replicas: each write is conditional on the stored value being unchanged, so a concurrent save wins (it is
/// already encrypted with the current key). Values no configured key decrypts are counted and logged by id, never
/// changed. A keyed recipient hash of a definition the recipient does not hold cannot be recomputed (its plaintext is not
/// stored): while a previous key is configured it is counted as an unresolved key dependency and logged by id, until the
/// recipient's pending change is decided (an approval writes the approved hash under the current key) or the recipient is
/// saved back to its approved destination. Once a run reports no value left under a previous key, none unreadable and no
/// unresolved key dependency, the previous keys can be removed from the configuration. The sample host runs it as a
/// background job at startup while a previous key is configured.
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
    /// Keyed recipient and data-source definition hashes (approved, or of a pending change request) that name a definition
    /// the recipient or data source does not hold, and pending LLM connection changes whose recorded hash or shown
    /// fingerprints are not under the current key, so they cannot be rewritten under the current key and may still need a
    /// previous key; counted only while a previous key is configured. The previous keys are not removable while any remain.
    /// </summary>
    public int UnresolvedKeyDependencies { get; init; }

    /// <summary>True when nothing stored needs a previous key any more: the previous keys can be removed.</summary>
    public bool PreviousKeysRemovable => UnderPreviousKeys == 0 && Unreadable == 0 && Failed == 0 && UnresolvedKeyDependencies == 0;
}

internal sealed class StoredValueReEncryptionService(
    IDbContextFactory<BeaconContext> contextFactory,
    IEncryptionService encryptionService,
    ILogger<StoredValueReEncryptionService> logger,
    RecipientDestinationHasher? hasher = null,
    IDataSourceDefinitionReader? definitionReader = null,
    ConnectionDefinitionHasher? connectionHasher = null,
    LlmConnectionApproval? llmConnections = null) : IStoredValueReEncryptionService
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
        if (!result.PreviousKeysRemovable)
        {
            logger.LogWarning(
                ProjectAccessEvents.StoredValuesReEncrypted,
                "Stored value re-encryption: {UnderPreviousKeys} under a previous key, {ReEncrypted} re-encrypted with the current key, {Skipped} changed meanwhile, {Unreadable} readable with no configured key, {Failed} failed, {Unresolved} keyed hashes that cannot be rewritten under the current key",
                result.UnderPreviousKeys,
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
                "Stored value re-encryption: no value is under a previous key; Beacon:PreviousEncryptionKeys can be removed");
        }

        if (result.UnresolvedKeyDependencies > 0)
        {
            logger.LogWarning(
                ProjectAccessEvents.StoredValuesReEncrypted,
                "{Unresolved} keyed hashes name a definition their recipient or data source does not hold, or belong to a pending LLM connection change, and may still need a previous key; keep {Setting} until none remain. Decide each pending change (an approval writes its approved hash under the current key), or save the recipient or data source back to its approved definition, then run again",
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

        // A staged LLM connection change that waited past its expiry is wiped, not re-encrypted.
        if (run.Writes && llmConnections != null)
        {
            await llmConnections.ExpireAsync(context, cancellationToken);
        }

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

        var payloads = await context.ChangeApprovalRequests
            .Where(x => x.EncryptedPayload != null)
            .Select(x =>
                new
                {
                    x.Id,
                    x.EncryptedPayload
                })
            .ToListAsync(cancellationToken);
        foreach (var row in payloads)
        {
            await run.ReEncryptAsync("LLM connection change request", row.Id, row.EncryptedPayload, string.Empty, value => context.ChangeApprovalRequests
                .Where(x => x.Id == row.Id)
                .Where(x => x.EncryptedPayload == row.EncryptedPayload)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.EncryptedPayload, value), cancellationToken), cancellationToken);
        }

        if (hasher != null)
        {
            await RehashRecipientApprovalsAsync(context, run, hasher, new RecipientSecretProtector(run.Encryption), cancellationToken);
        }

        if (definitionReader != null && connectionHasher != null)
        {
            await RehashDataSourceApprovalsAsync(context, run, definitionReader, connectionHasher, cancellationToken);
        }

        if (llmConnections != null)
        {
            var llmDependencies = await llmConnections.KeyDependenciesAsync(context, cancellationToken);
            foreach (var requestId in llmDependencies)
            {
                run.UnresolvedRequest(true, "LLM connection change request", requestId);
            }
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

    // The keyed hashes of governed data sources' connection definitions (approved, and of their pending requests) that match
    // the definition the source holds only under a previous key: recomputed under the current key from its stored
    // connection (through the definition reader, the one place a stored connection becomes a definition hash), each
    // conditional on being unchanged. A held source has no keyed hash. One that names a definition the source does not
    // hold (a change waits, or the stored connection cannot be read) cannot be recomputed: while a previous key is
    // configured it counts as an unresolved key dependency.
    private static async Task RehashDataSourceApprovalsAsync(
        BeaconContext context,
        Run run,
        IDataSourceDefinitionReader reader,
        ConnectionDefinitionHasher hasher,
        CancellationToken cancellationToken)
    {
        // Full entities: the definition reader decrypts the stored connection itself.
        var governed = await context.DataSources
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.ApprovedTargetHash != null)
            .ToListAsync(cancellationToken);
        var sources = governed.ToDictionary(x => x.Id);

        foreach (var source in governed)
        {
            var approved = source.ApprovedTargetHash;
            if (!IsKeyedDefinition(approved))
            {
                continue;
            }

            var current = reader.CurrentDefinitionHash(source);
            if (string.Equals(current, approved, StringComparison.Ordinal))
            {
                continue;
            }

            if (current == null || !reader.DefinitionMatches(source, approved))
            {
                run.UnresolvedRequest(hasher.HasPreviousKeys, "data source approval", source.Id);
                continue;
            }

            await run.RewriteAsync("data source approval", source.Id, () => context.DataSources
                .IgnoreQueryFilters()
                .Where(x => x.Id == source.Id)
                .Where(x => x.ApprovedTargetHash == approved)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.ApprovedTargetHash, current), cancellationToken), cancellationToken);
        }

        var requests = await context.ChangeApprovalRequests
            .Where(x => x.Kind == ChangeApprovalKind.DataSourceConnection)
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
            if (!IsKeyedDefinition(request.DefinitionHash))
            {
                continue;
            }

            var source = sources.GetValueOrDefault(request.ResourceId);
            var current = source == null ? null : reader.CurrentDefinitionHash(source);
            if (string.Equals(current, request.DefinitionHash, StringComparison.Ordinal))
            {
                continue;
            }

            if (source == null || current == null || !reader.DefinitionMatches(source, request.DefinitionHash))
            {
                run.UnresolvedRequest(hasher.HasPreviousKeys, "data source change request", request.Id);
                continue;
            }

            await run.RewriteAsync("data source change request", request.Id, () => context.ChangeApprovalRequests
                .Where(x => x.Id == request.Id)
                .Where(x => x.DefinitionHash == request.DefinitionHash)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.DefinitionHash, current), cancellationToken), cancellationToken);
        }
    }

    // A keyed connection definition hash, as opposed to a held source's marker.
    private static bool IsKeyedDefinition(string? hash)
    {
        return hash != null && hash.StartsWith(ConnectionDefinitionHasher.Prefix, StringComparison.Ordinal);
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

        /// <summary>True when the run writes (re-encrypts), false when it only counts.</summary>
        public bool Writes => write;

        private int _underPreviousKeys;
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

        // A keyed hash (or a fingerprint a request shows) that cannot be rewritten under the current key: while a previous
        // key is configured it may depend on one, so it is counted and, with write, logged by id.
        public void UnresolvedRequest(bool previousKeysConfigured, string kind, int id)
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
                "The keyed hash of {Kind} {Id} cannot be rewritten under the current key and may still need a previous key",
                kind,
                id);
        }

        public StoredValueReEncryptionResult Result()
        {
            return new StoredValueReEncryptionResult(_underPreviousKeys, _reEncrypted, _skipped, _unreadable, _failed)
            {
                UnresolvedKeyDependencies = _unresolved
            };
        }
    }
}
