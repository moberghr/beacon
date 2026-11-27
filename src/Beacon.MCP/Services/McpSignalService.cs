using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Helpers;
using Beacon.Core.Services;
using Beacon.Core.Services.Retention;

namespace Beacon.MCP.Services;

internal sealed class McpSignalService(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider,
    ILogger<McpSignalService> logger,
    TimeProvider? timeProvider = null)
{
    private const string AskTool = "ask";

    // How long after an ask a query or dry_run on the same data source, by the same caller, still counts as following it.
    private const int ImplicitParentWindowMinutes = 15;

    // The tools whose signals link to the ask signal they followed.
    private static readonly IReadOnlySet<string> ParentLinkedTools = new HashSet<string>(StringComparer.Ordinal) { "query", "dry_run" };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // Returns the persisted signal's Id (populated post-save) so callers can surface it for human feedback,
    // or null when learning is disabled or recording failed. Recording stays non-optional and best-effort:
    // a transient failure is swallowed + logged, never propagated to the tool call.
    public async Task<int?> RecordSignalAsync(McpQuerySignal signal, CancellationToken ct = default)
    {
        try
        {
            var settings = await settingsProvider.GetEffectiveSettingsAsync(signal.ProjectId ?? 0, ct);
            if (!settings.EnableLearning) return null;

            if (!ContentRetentionDecision.From(settings).RetainQueryContent)
            {
                McpContentRedactor.RedactSignal(signal);
            }

            // The incoming ParentSignalId is the caller's unverified REQUEST (McpSignalBuilder carries signal_id there, as
            // McpQuerySignal has no field for it): it is always replaced with the parent resolved below, or null.
            var requestedParentId = signal.ParentSignalId;
            signal.ParentSignalId = null;
            signal.IsEditedRerun = false;

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            await LinkParentAsync(context, signal, requestedParentId, ct);
            context.McpQuerySignals.Add(signal);
            await context.SaveChangesAsync(ct);
            return signal.Id;
        }
        catch (Exception ex)
        {
            // §1.7/§9.5 — signal recording is non-optional. Swallow so a transient DB issue doesn't fail
            // the tool call, but log at Error so a sustained outage of the learning loop is visible.
            logger.LogError("Failed to record MCP query signal ({Failure})", FailureCodes.Describe(ex));
            return null;
        }
    }

    // Links a query or dry_run signal to the ask signal it followed, after redaction (a content-locked project gets the link
    // but never the edited flag: its CandidateSqlHash is gone). The parent is looked up only among the ask signals of the
    // signal's own project and caller, in SQL (§1.13): a requested id outside them matches nothing, and nothing the caller
    // can observe changes. A failed lookup leaves the signal unlinked and is logged by ids only (§1.11); the signal is still
    // saved. The caller's own cancellation reaches the save path's handling.
    private async Task LinkParentAsync(BeaconContext context, McpQuerySignal signal, int? requestedParentId, CancellationToken ct)
    {
        if (!ParentLinkedTools.Contains(signal.Tool) || signal.ProjectId == null)
        {
            return;
        }

        // Same caller: the mapped caller's hash when the signal has one, else its Beacon user (API keys, sessions).
        var callerHash = string.IsNullOrEmpty(signal.CallerHash) ? null : signal.CallerHash;
        if (callerHash == null && signal.UserId == null)
        {
            return;
        }

        try
        {
            var parent = await FindParentAsync(context, signal, callerHash, requestedParentId, ct);
            if (parent == null)
            {
                return;
            }

            signal.ParentSignalId = parent.Id;
            signal.IsEditedRerun = parent.CandidateSqlHash != null
                && signal.CandidateSqlHash != null
                && !string.Equals(parent.CandidateSqlHash, signal.CandidateSqlHash, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "Failed to link MCP {Tool} signal of project {ProjectId}, data source {DataSourceId} to its ask signal ({Failure})",
                signal.Tool,
                signal.ProjectId,
                signal.DataSourceId,
                FailureCodes.Describe(ex));
        }
    }

    private async Task<ParentAsk?> FindParentAsync(
        BeaconContext context, McpQuerySignal signal, string? callerHash, int? requestedParentId, CancellationToken ct)
    {
        var projectId = signal.ProjectId!.Value;
        var userId = signal.UserId;
        var askSignals = context.McpQuerySignals
            .Where(x => x.Tool == AskTool)
            .Where(x => x.ProjectId == projectId);
        askSignals = callerHash != null
            ? askSignals.Where(x => x.CallerHash == callerHash)
            : askSignals.Where(x => x.UserId == userId);

        if (requestedParentId != null)
        {
            var parentId = requestedParentId.Value;
            var requested = await askSignals
                .Where(x => x.Id == parentId)
                .Select(x =>
                    new ParentAsk
                    {
                        Id = x.Id,
                        CandidateSqlHash = x.CandidateSqlHash
                    })
                .FirstOrDefaultAsync(ct);

            if (requested != null)
            {
                return requested;
            }
        }

        if (signal.DataSourceId == null)
        {
            return null;
        }

        var dataSourceId = signal.DataSourceId.Value;
        var cutoff = _time.GetUtcNow().UtcDateTime.AddMinutes(-ImplicitParentWindowMinutes);

        return await askSignals
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.CreatedTime >= cutoff)
            .OrderByDescending(x => x.Id)
            .Select(x =>
                new ParentAsk
                {
                    Id = x.Id,
                    CandidateSqlHash = x.CandidateSqlHash
                })
            .FirstOrDefaultAsync(ct);
    }

    private sealed class ParentAsk
    {
        public int Id { get; init; }

        public string? CandidateSqlHash { get; init; }
    }
}
