using System.Security.Cryptography;
using System.Text.Json;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.AdminSettings;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Models.Settings;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// Changes of the stored LLM connection under the approval workflow
/// (<see cref="ApprovalWorkflowOptions.CoversLlmConnection"/>). A save that changes the provider, endpoint, region or
/// authentication mode — or stores a first provider over the host's configuration — is staged: the whole resulting
/// connection (those four, the models and every credential) is encrypted on the request, the reviewer is shown its view
/// (the endpoint as scheme and host, every credential as a fingerprint), the other settings are saved in the same unit of
/// work, and the stored connection stays in use until a live Admin approves. A change of credentials or models alone
/// applies at once. The request names the stored connection it was made from by its keyed hash; an approval applies only
/// while the stored connection is still that one. Every save of the connection and every decision on a change of it take
/// the LLM connection lock (<see cref="ILlmConnectionLock"/>) before they read the stored connection, so they run one after
/// the other. A request expires after
/// <see cref="ApprovalWorkflowOptions.LlmConnectionRequestDays"/>, and its staged connection is wiped whenever it can no
/// longer be applied (decided, superseded, stale or expired). Logs carry request ids and counts only.
/// </summary>
internal sealed class LlmConnectionApproval(
    IDbContextFactory<BeaconContext> contextFactory,
    IAppSettingsService settingsService,
    IAppSettingsStager stager,
    ChangeApprovalSubmitter submitter,
    IEncryptionService encryption,
    ConnectionDefinitionHasher hasher,
    ILlmProviderCatalog providerCatalog,
    ISecurityAuditRecorder securityAudit,
    BeaconConfiguration configuration,
    ILlmConnectionLock connectionLock,
    ILogger<LlmConnectionApproval> logger)
{
    /// <summary>The one stored LLM connection's resource id.</summary>
    public const int ResourceId = 0;

    /// <summary>How the LLM connection is named on its requests.</summary>
    public const string ResourceName = "LLM connection";

    /// <summary>Why a change of the LLM connection that waited past its expiry is not decided.</summary>
    internal const string ExpiredMessage =
        "This change of the LLM connection expired before it was decided, and what it staged was wiped: make the change again.";

    /// <summary>Why approving a change of the LLM connection shown only by fingerprints needs a comment.</summary>
    internal const string CommentRequiredMessage =
        "This change of the LLM connection lies only in parts shown by their fingerprints (the endpoint's path or query): say in a comment how you checked it, for example by comparing the fingerprints with the requester.";

    private const ChangeApprovalKind Kind = ChangeApprovalKind.LlmConnection;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// The settings a save of the admin settings is judged against: the stored settings as they are now, never this
    /// process's cached copy, which can predate a change another process stored.
    /// </summary>
    public async Task<AppSettingsData> StoredAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await stager.ReadStoredAsync(context, cancellationToken);
    }

    /// <summary>
    /// Saves the admin settings <paramref name="next"/> (read from <paramref name="current"/>, the stored settings,
    /// <see cref="StoredAsync"/>). Without the workflow covering
    /// the LLM connection they are saved at once (under the LLM connection lock too); a save that changes the stored
    /// connection then supersedes the pending requests it makes moot. Under the workflow the change is judged against the
    /// stored connection inside the unit of work that writes it, under the LLM connection lock: a change of credentials or
    /// models alone is saved at once, any other change of the connection is staged for approval (403 for a requester Beacon
    /// cannot name, 409 while someone else's change waits or when another change is submitted at the same time) and the rest
    /// is saved with the request.
    /// </summary>
    public async Task SaveAsync(AppSettingsData current, AppSettingsData next, string? changedByUserId, bool saveLimits, CancellationToken cancellationToken)
    {
        var proposed = LlmConnectionDefinition.Of(next);
        if (!configuration.ApprovalWorkflow.Covers(Kind))
        {
            await SaveLockedAsync(next, changedByUserId, saveLimits, cancellationToken);
            if (!string.Equals(LlmConnectionDefinition.Of(current).Canonical(), proposed.Canonical(), StringComparison.Ordinal))
            {
                await SupersedeOrphanedAsync(cancellationToken);
            }

            return;
        }

        var payload = encryption.Encrypt(JsonSerializer.Serialize(proposed, JsonOptions));

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        StagedAppSettings? staged = null;
        ChangeSubmission? submission = null;
        try
        {
            // One unit of work: the stored connection read, the change judged against it, and either the whole save or
            // expired requests out of the way, the request (superseding the requester's own pending one), the other
            // settings and the events, committed together or not at all.
            var strategy = context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                staged = null;
                submission = null;
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                // The lock first, as an approval takes it: no approval checks or writes the stored connection between this
                // read and this commit.
                await connectionLock.AcquireAsync(context, cancellationToken);
                var stored = await stager.ReadStoredAsync(context, cancellationToken);
                var fromHost = stored.LlmProvider == null && proposed.Provider != null;
                var storedConnection = LlmConnectionDefinition.Of(stored);
                var governed = LlmConnectionDefinition.GovernedChanges(fromHost ? null : storedConnection, proposed);
                if (governed.Count == 0)
                {
                    staged = await stager.StageAsync(context, next, changedByUserId, AppSettingsParts.Everything, saveLimits, cancellationToken);
                    await securityAudit.SaveWithEventsAsync(context, staged.AuditEntries, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    return;
                }

                var requester = await submitter.RequesterAsync(Kind, ResourceId, cancellationToken)
                    ?? throw new InvalidOperationException("The approval workflow does not cover the LLM connection.");

                await ExpireAsync(context, cancellationToken);
                submission = await submitter.SubmitAsync(context, requester, Proposal(storedConnection, proposed, fromHost, governed, payload), cancellationToken);
                staged = await stager.StageAsync(context, next, changedByUserId, AppSettingsParts.AllButConnection, saveLimits, cancellationToken);

                await securityAudit.SaveWithEventsAsync(
                    context,
                    () => [.. staged.AuditEntries(), .. ChangeApprovalAuditEntries.Submitted(submission)],
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            });
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            logger.LogWarning("Change of the LLM connection refused: another change of it was submitted at the same time");

            throw new BeaconConflictException(ChangeApprovalSubmitter.OtherPendingMessage(Kind));
        }

        if (submission != null)
        {
            logger.LogInformation("Change request {RequestId} of the LLM connection submitted for approval", submission.Request?.Id);
        }

        // The save is committed: a failure to apply it in this process is logged (the exception type only), never a failed
        // request that a retry would meet as a conflict.
        try
        {
            await staged!.AfterCommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                "The settings save was committed (change request {RequestId} of the LLM connection {Outcome}), but could not be applied in this process ({ExceptionType}); it applies at the next start",
                submission?.Request?.Id,
                submission == null ? "not needed" : "submitted",
                ex.GetType().Name);
        }

        await submitter.NotifySupersededAsync(submission, cancellationToken);
    }

    /// <summary>
    /// Takes the LLM connection lock (<see cref="ILlmConnectionLock"/>) in <paramref name="context"/>'s open transaction: a
    /// decision on a change of the LLM connection takes it before it checks the stored connection, as every save does.
    /// </summary>
    public Task LockAsync(BeaconContext context, CancellationToken cancellationToken)
    {
        return connectionLock.AcquireAsync(context, cancellationToken);
    }

    /// <summary>True when a request created at <paramref name="createdTime"/> waited past its expiry.</summary>
    public bool IsExpired(DateTime createdTime)
    {
        return createdTime < ExpiryCutoff();
    }

    /// <summary>
    /// Every pending LLM connection request that waited past its expiry is superseded, its staged connection wiped, in
    /// <paramref name="context"/> at once; answers how many.
    /// </summary>
    public async Task<int> ExpireAsync(BeaconContext context, CancellationToken cancellationToken)
    {
        var cutoff = ExpiryCutoff();
        var expired = await context.ChangeApprovalRequests
            .Where(x => x.Kind == Kind)
            .Where(x => x.Status == ApprovalStatus.Pending)
            .Where(x => x.CreatedTime < cutoff)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(y => y.Status, ApprovalStatus.Superseded)
                    .SetProperty(y => y.EncryptedPayload, (string?)null),
                cancellationToken);
        if (expired > 0)
        {
            logger.LogInformation("{RequestCount} pending changes of the LLM connection expired; their staged connections were wiped", expired);
        }

        return expired;
    }

    /// <summary>
    /// Request <paramref name="requestId"/> waited past its expiry: superseded, its staged connection wiped, and refused
    /// (409). Nothing happens to a request that has not expired or is no longer pending.
    /// </summary>
    public async Task RefuseIfExpiredAsync(BeaconContext context, int requestId, CancellationToken cancellationToken)
    {
        var cutoff = ExpiryCutoff();
        var expired = await context.ChangeApprovalRequests
            .Where(x => x.Id == requestId)
            .Where(x => x.Kind == Kind)
            .Where(x => x.Status == ApprovalStatus.Pending)
            .Where(x => x.CreatedTime < cutoff)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(y => y.Status, ApprovalStatus.Superseded)
                    .SetProperty(y => y.EncryptedPayload, (string?)null),
                cancellationToken);
        if (expired == 0)
        {
            return;
        }

        logger.LogInformation("Change request {RequestId} of the LLM connection expired before it was decided; its staged connection was wiped", requestId);

        throw new BeaconConflictException(ExpiredMessage);
    }

    /// <summary>
    /// A request found stale at approval (the stored connection changed since it was made): superseded from Pending, its
    /// staged connection wiped, after the decision's transaction rolled back.
    /// </summary>
    public async Task SupersedeStaleAsync(BeaconContext context, int requestId, CancellationToken cancellationToken)
    {
        context.ChangeTracker.Clear();
        var superseded = await context.ChangeApprovalRequests
            .Where(x => x.Id == requestId)
            .Where(x => x.Kind == Kind)
            .Where(x => x.Status == ApprovalStatus.Pending)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(y => y.Status, ApprovalStatus.Superseded)
                    .SetProperty(y => y.EncryptedPayload, (string?)null),
                cancellationToken);
        if (superseded > 0)
        {
            logger.LogInformation("Change request {RequestId} of the LLM connection was superseded: the stored connection changed since it was made", requestId);
        }
    }

    /// <summary>
    /// The keyed hash of the stored connection as a request records it: <paramref name="requestedHash"/> when the stored
    /// connection is the one it names (under the current key or a previous one), else the stored connection's hash under
    /// the current key.
    /// </summary>
    public async Task<string> StateAsync(BeaconContext context, string requestedHash, CancellationToken cancellationToken)
    {
        var canonical = LlmConnectionDefinition.Of(await stager.ReadStoredAsync(context, cancellationToken)).Canonical();

        return hasher.Matches(ConnectionDefinitionPurpose.LlmConnection, requestedHash, canonical)
            ? requestedHash
            : hasher.Hash(ConnectionDefinitionPurpose.LlmConnection, canonical);
    }

    /// <summary>
    /// The staged connection of request <paramref name="requestId"/>, read before anything is decided: 409 when it is
    /// absent or cannot be read, or is not exactly what <paramref name="proposedJson"/> showed the reviewer; 400 when its
    /// provider is not installed. The request stays pending either way.
    /// </summary>
    public async Task<LlmConnectionDefinition> ProposalToApplyAsync(
        BeaconContext context,
        int requestId,
        string proposedJson,
        CancellationToken cancellationToken)
    {
        var payload = await context.ChangeApprovalRequests
            .Where(x => x.Id == requestId)
            .Select(x => x.EncryptedPayload)
            .FirstOrDefaultAsync(cancellationToken);
        var proposed = Decrypt(payload);
        var shown = Deserialize<LlmConnectionView>(proposedJson);
        if (proposed == null || shown == null || !proposed.IsShownBy(shown, hasher, currentKeyOnly: false))
        {
            logger.LogWarning("Change request {RequestId} of the LLM connection refused: its staged connection is missing, cannot be read or is not the one shown", requestId);

            throw new BeaconConflictException(ChangeApprovalService.UnreadableProposalMessage);
        }

        var stored = await stager.ReadStoredAsync(context, cancellationToken);
        LlmProviderInstallation.DemandInstalled(providerCatalog, proposed.Provider, stored.LlmProvider);

        return proposed;
    }

    /// <summary>
    /// Writes <paramref name="proposed"/> (read by <see cref="ProposalToApplyAsync"/>) in the decision's unit of work: the
    /// connection and models, their history attributed to the requester, and wipes the request's staged connection. The
    /// caller saves with the staged save's event and, after the commit, calls its
    /// <see cref="StagedAppSettings.AfterCommitAsync"/>. The limits are never written.
    /// </summary>
    public async Task<StagedAppSettings> ApplyAsync(
        BeaconContext context,
        int requestId,
        LlmConnectionDefinition proposed,
        string? requestedByUserId,
        CancellationToken cancellationToken)
    {
        var staged = await stager.StageAsync(context, proposed.ToSettings(), requestedByUserId, AppSettingsParts.ConnectionOnly, saveLimits: false, cancellationToken);
        await context.ChangeApprovalRequests
            .Where(x => x.Id == requestId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.EncryptedPayload, (string?)null), cancellationToken);

        return staged;
    }

    /// <summary>
    /// True when <paramref name="proposed"/> differs from <paramref name="previous"/> only in parts shown by their
    /// fingerprints (the endpoint's path or query): approving it needs a comment. False for a first stored connection.
    /// </summary>
    public static bool ChangeOnlyInFingerprints(LlmConnectionView? previous, LlmConnectionView? proposed)
    {
        return previous != null
            && proposed != null
            && !previous.FromHostConfiguration
            && previous.Provider == proposed.Provider
            && string.Equals(previous.Endpoint, proposed.Endpoint, StringComparison.Ordinal)
            && string.Equals(previous.Region, proposed.Region, StringComparison.Ordinal)
            && previous.BedrockAuthMode == proposed.BedrockAuthMode;
    }

    /// <summary>
    /// The pending requests whose recorded hash of the stored connection, or whose shown fingerprints, are not under the
    /// current key while a previous key is configured: they cannot be rewritten (the reviewer's view is bound by its payload
    /// hash), so they may still need a previous key until they are decided, expire or are submitted again. Their ids;
    /// none while no previous key is configured.
    /// </summary>
    public async Task<IReadOnlyList<int>> KeyDependenciesAsync(BeaconContext context, CancellationToken cancellationToken)
    {
        if (!hasher.HasPreviousKeys)
        {
            return [];
        }

        var pending = await context.ChangeApprovalRequests
            .Where(x => x.Kind == Kind)
            .Where(x => x.Status == ApprovalStatus.Pending)
            .Select(x =>
                new
                {
                    x.Id,
                    x.DefinitionHash,
                    x.ProposedJson,
                    x.EncryptedPayload
                })
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return [];
        }

        var canonical = LlmConnectionDefinition.Of(await stager.ReadStoredAsync(context, cancellationToken)).Canonical();

        return pending
            .Where(x => !hasher.IsCurrent(ConnectionDefinitionPurpose.LlmConnection, x.DefinitionHash, canonical)
                || !ShownUnderCurrentKey(x.EncryptedPayload, x.ProposedJson))
            .Select(x => x.Id)
            .ToList();
    }

    // The request a governed change of the stored connection makes, naming the stored connection by its keyed hash and
    // carrying the staged connection encrypted.
    private ChangeProposal Proposal(LlmConnectionDefinition stored, LlmConnectionDefinition proposed, bool fromHost, IReadOnlyList<string> governed, string payload)
    {
        return new ChangeProposal(
            Kind,
            ResourceId,
            ResourceName,
            PreviousDefinitionHash: null,
            DefinitionHash: stored.Hash(hasher),
            NeedsApproval: true,
            Proposed: proposed.View(hasher, stored.Fingerprint(hasher)),
            Previous: fromHost ? LlmConnectionDefinition.HostConfigurationView() : stored.View(hasher, null),
            ChangeSummary: string.Join(", ", governed) + " changed",
            MatchesDefinition: _ => false,
            EncryptedPayload: payload);
    }

    // A save while the workflow does not cover the LLM connection, through the settings service, under the LLM connection
    // lock held by a transaction of its own until the save is done: an approval of a request made while it did (decisions
    // stay possible) never checks the stored connection before the save and writes after it.
    private async Task SaveLockedAsync(AppSettingsData next, string? changedByUserId, bool saveLimits, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            await connectionLock.AcquireAsync(context, cancellationToken);
            await settingsService.SaveSettingsAsync(next, changedByUserId, saveLimits, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    // A save made while the workflow does not cover the LLM connection changed the stored connection: every pending request
    // was made from another one, so it can no longer be applied (superseded, its staged connection wiped). After the save,
    // in a unit of its own: one that fails leaves the requests stale, and an approval refuses and supersedes them then.
    private async Task SupersedeOrphanedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var submission = await submitter.SupersedeOrphanedAsync(context, Kind, ResourceId, null, cancellationToken);
            await submitter.NotifySupersededAsync(submission, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "The LLM connection was saved, but its pending change requests could not be superseded ({ExceptionType}); an approval of one refuses it as stale",
                ex.GetType().Name);
        }
    }

    private DateTime ExpiryCutoff()
    {
        return DateTime.UtcNow.AddDays(-configuration.ApprovalWorkflow.LlmConnectionRequestDays);
    }

    // Whether the staged connection is readable and its view, as the request shows it, holds only current-key fingerprints.
    private bool ShownUnderCurrentKey(string? payload, string proposedJson)
    {
        var proposed = Decrypt(payload);
        var shown = Deserialize<LlmConnectionView>(proposedJson);

        return proposed != null && shown != null && proposed.IsShownBy(shown, hasher, currentKeyOnly: true);
    }

    // The staged connection; null when there is none or it cannot be decrypted or read.
    private LlmConnectionDefinition? Decrypt(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LlmConnectionDefinition>(encryption.Decrypt(payload), JsonOptions);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
