using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;

namespace Beacon.Core.Services;

public interface IMcpSettingsProvider
{
    /// <summary>Global row with deployment locks and ceilings applied — for consumers that have no project.</summary>
    Task<McpSettingsData> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>Resolved for one project: deployment lock → project value → global value → code default, clamped to the ceilings.</summary>
    Task<McpSettingsData> GetEffectiveSettingsAsync(int projectId, CancellationToken ct = default);

    /// <summary>Same as <see cref="GetEffectiveSettingsAsync"/> plus which fields a lock pinned and which a ceiling lowered.</summary>
    Task<McpEffectiveSettings> GetEffectiveSettingsDetailAsync(int projectId, CancellationToken ct = default);

    /// <summary>The project's stored overrides (nulls = inherit), or null when the project has no row.</summary>
    Task<McpProjectSettingsData?> GetProjectOverridesAsync(int projectId, CancellationToken ct = default);

    void InvalidateCache();
}

internal sealed class McpSettingsProvider(
    IDbContextFactory<BeaconContext> contextFactory,
    IMemoryCache cache,
    IOptions<McpDeploymentOptions> deploymentOptions,
    ILogger<McpSettingsProvider> logger) : IMcpSettingsProvider
{
    private const string GlobalCacheKey = "McpSettings";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    // One reset token for every cache entry this provider creates. STATIC because the provider is registered
    // transient (ServiceConfiguration): an instance field would only ever invalidate the entries the same
    // instance created, so a write on one request would leave every other cached copy stale.
    private static CancellationTokenSource _resetSource = new();
    private static readonly object ResetLock = new();

    public async Task<McpSettingsData> GetSettingsAsync(CancellationToken ct = default)
    {
        var global = await GetGlobalRawAsync(ct);

        return Resolve(global, null, deploymentOptions.Value).Effective;
    }

    public async Task<McpSettingsData> GetEffectiveSettingsAsync(int projectId, CancellationToken ct = default)
    {
        var detail = await GetEffectiveSettingsDetailAsync(projectId, ct);

        return detail.Effective;
    }

    public async Task<McpEffectiveSettings> GetEffectiveSettingsDetailAsync(int projectId, CancellationToken ct = default)
    {
        var global = await GetGlobalRawAsync(ct);
        var project = projectId > 0 ? await GetProjectRawAsync(projectId, ct) : null;

        return Resolve(global, project, deploymentOptions.Value);
    }

    public Task<McpProjectSettingsData?> GetProjectOverridesAsync(int projectId, CancellationToken ct = default)
    {
        return projectId > 0
            ? GetProjectRawAsync(projectId, ct)
            : Task.FromResult<McpProjectSettingsData?>(null);
    }

    public void InvalidateCache()
    {
        CancellationTokenSource previous;
        lock (ResetLock)
        {
            previous = _resetSource;
            _resetSource = new CancellationTokenSource();
        }

        // Cancel but never Dispose: an entry whose factory captured the previous token and commits after this
        // point still registers its expiration callback on it (ConfigurationReloadToken pattern). The
        // cancelled source is collected once the cache drops its callbacks.
        previous.Cancel();
    }

    /// <summary>
    /// Pure resolution: code defaults ← global row ← non-null project overrides ← ceilings (lower only) ← locks
    /// (always last, so no layer can override them). Internal so the precedence tests can drive it directly.
    /// </summary>
    internal static McpEffectiveSettings Resolve(McpSettingsData global, McpProjectSettingsData? project, McpDeploymentOptions options)
    {
        var effective = global.Clone();
        var locked = new HashSet<string>(StringComparer.Ordinal);
        var clamped = new HashSet<string>(StringComparer.Ordinal);

        if (project != null)
        {
            OverlayProject(effective, project);
        }

        ApplyCeilings(effective, options.Ceilings ?? new McpCeilingOptions(), clamped);
        ApplyLocks(effective, options, locked);

        return new McpEffectiveSettings(effective, locked, clamped);
    }

    private static void OverlayProject(McpSettingsData effective, McpProjectSettingsData project)
    {
        effective.MaxRowLimit = project.MaxRowLimit ?? effective.MaxRowLimit;
        effective.EnforceReadOnly = project.EnforceReadOnly ?? effective.EnforceReadOnly;
        effective.EnablePiiDetection = project.EnablePiiDetection ?? effective.EnablePiiDetection;
        effective.CustomPiiPatterns = project.CustomPiiPatterns != null ? [.. project.CustomPiiPatterns] : effective.CustomPiiPatterns;
        effective.EnableSampleValueCollection = project.EnableSampleValueCollection ?? effective.EnableSampleValueCollection;

        effective.EnableLearning = project.EnableLearning ?? effective.EnableLearning;
        effective.LearningAutoApproveThreshold = project.LearningAutoApproveThreshold ?? effective.LearningAutoApproveThreshold;
        effective.LearningInjectionBudgetChars = project.LearningInjectionBudgetChars ?? effective.LearningInjectionBudgetChars;
        effective.LearningSignalRetentionDays = project.LearningSignalRetentionDays ?? effective.LearningSignalRetentionDays;

        effective.EnableSelfConsistency = project.EnableSelfConsistency ?? effective.EnableSelfConsistency;
        effective.SelfConsistencyCandidateCount = project.SelfConsistencyCandidateCount ?? effective.SelfConsistencyCandidateCount;
        effective.EnableEvalJudge = project.EnableEvalJudge ?? effective.EnableEvalJudge;
        effective.EnableSemanticRetrieval = project.EnableSemanticRetrieval ?? effective.EnableSemanticRetrieval;
        effective.ExemplarTopK = project.ExemplarTopK ?? effective.ExemplarTopK;

        effective.EnableReplayVerification = project.EnableReplayVerification ?? effective.EnableReplayVerification;
        effective.LearningReplayMinFlips = project.LearningReplayMinFlips ?? effective.LearningReplayMinFlips;

        effective.EnableContextualRetrieval = project.EnableContextualRetrieval ?? effective.EnableContextualRetrieval;
        effective.DocChunkWindowSentences = project.DocChunkWindowSentences ?? effective.DocChunkWindowSentences;
        effective.DocChunkOverlapSentences = project.DocChunkOverlapSentences ?? effective.DocChunkOverlapSentences;
        effective.GlossaryTopK = project.GlossaryTopK ?? effective.GlossaryTopK;
        effective.DocChunkTopK = project.DocChunkTopK ?? effective.DocChunkTopK;

        effective.EnableGoldenExemplars = project.EnableGoldenExemplars ?? effective.EnableGoldenExemplars;
        effective.GoldenExemplarTopK = project.GoldenExemplarTopK ?? effective.GoldenExemplarTopK;
        effective.GoldenExemplarBudgetChars = project.GoldenExemplarBudgetChars ?? effective.GoldenExemplarBudgetChars;

        effective.EnableValueGrounding = project.EnableValueGrounding ?? effective.EnableValueGrounding;
        effective.ValueGroundingMaxProbes = project.ValueGroundingMaxProbes ?? effective.ValueGroundingMaxProbes;
        effective.EnableSemanticLint = project.EnableSemanticLint ?? effective.EnableSemanticLint;
        effective.SelfConsistencyMinTables = project.SelfConsistencyMinTables ?? effective.SelfConsistencyMinTables;

        effective.RetainQueryContent = project.RetainQueryContent ?? effective.RetainQueryContent;
        effective.StatementTimeoutSeconds = project.StatementTimeoutSeconds ?? effective.StatementTimeoutSeconds;
        effective.MaxResultBytes = project.MaxResultBytes ?? effective.MaxResultBytes;
        effective.MaxExplainCost = project.MaxExplainCost ?? effective.MaxExplainCost;
        effective.MaxConcurrentQueriesPerKey = project.MaxConcurrentQueriesPerKey ?? effective.MaxConcurrentQueriesPerKey;
        effective.AllowExplicitFeedbackContent = project.AllowExplicitFeedbackContent ?? effective.AllowExplicitFeedbackContent;
    }

    private static void ApplyCeilings(McpSettingsData effective, McpCeilingOptions ceilings, HashSet<string> clamped)
    {
        effective.MaxRowLimit = Clamp(effective.MaxRowLimit, ceilings.MaxRowLimit, nameof(effective.MaxRowLimit), clamped);
        effective.StatementTimeoutSeconds = Clamp(effective.StatementTimeoutSeconds, ceilings.StatementTimeoutSeconds, nameof(effective.StatementTimeoutSeconds), clamped);
        effective.MaxResultBytes = Clamp(effective.MaxResultBytes, ceilings.MaxResultBytes, nameof(effective.MaxResultBytes), clamped);
        effective.MaxConcurrentQueriesPerKey = Clamp(effective.MaxConcurrentQueriesPerKey, ceilings.MaxConcurrentQueriesPerKey, nameof(effective.MaxConcurrentQueriesPerKey), clamped);

        // A null MaxExplainCost means "no limit", which is above any ceiling — the ceiling becomes the value.
        if (ceilings.MaxExplainCost is { } costCeiling && (effective.MaxExplainCost == null || effective.MaxExplainCost > costCeiling))
        {
            effective.MaxExplainCost = costCeiling;
            clamped.Add(nameof(effective.MaxExplainCost));
        }
    }

    private static int Clamp(int value, int? ceiling, string field, HashSet<string> clamped)
    {
        if (ceiling is { } max && value > max)
        {
            clamped.Add(field);

            return max;
        }

        return value;
    }

    private static void ApplyLocks(McpSettingsData effective, McpDeploymentOptions options, HashSet<string> locked)
    {
        // One table drives resolution AND both write handlers (McpLockPolicy) — a new lockable field is one entry there.
        foreach (var rule in McpLockPolicy.ActiveRules(options))
        {
            rule.Pin(effective);
            locked.Add(rule.FieldName);
        }
    }

    private async Task<McpSettingsData> GetGlobalRawAsync(CancellationToken ct)
    {
        return await cache.GetOrCreateAsync(GlobalCacheKey, async entry =>
        {
            ConfigureEntry(entry);

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var entity = await context.McpSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);

            return entity != null ? MapToData(entity) : new McpSettingsData();
        }) ?? new McpSettingsData();
    }

    private async Task<McpProjectSettingsData?> GetProjectRawAsync(int projectId, CancellationToken ct)
    {
        return await cache.GetOrCreateAsync($"{GlobalCacheKey}:{projectId}", async entry =>
        {
            ConfigureEntry(entry);

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var entity = await context.McpProjectSettings
                .Where(x => x.ProjectId == projectId)
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);

            return entity != null ? MapToProjectData(entity) : null;
        });
    }

    private static void ConfigureEntry(ICacheEntry entry)
    {
        entry.AbsoluteExpirationRelativeToNow = CacheDuration;
        entry.AddExpirationToken(new CancellationChangeToken(_resetSource.Token));
    }

    private List<string>? ParsePiiPatterns(string? json, string owner, int id)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException ex)
        {
            // A corrupt row must not silently drop protection; log the identifier (never the payload) so the
            // misconfiguration is discoverable. Global falls back to "no custom patterns", a project row falls
            // back to inheriting the global list.
            logger.LogError(ex, "Failed to deserialize CustomPiiPatterns for {Owner} settings row {SettingsId}; custom PII patterns are disabled until fixed.", owner, id);

            return null;
        }
    }

    private McpSettingsData MapToData(McpSettings entity)
    {
        return new McpSettingsData
        {
            AskSystemPrompt = entity.AskSystemPrompt,
            GlobalInstruction = entity.GlobalInstruction,
            GetContextDescription = entity.GetContextDescription,
            SearchDescription = entity.SearchDescription,
            QueryDescription = entity.QueryDescription,
            GetDocumentationDescription = entity.GetDocumentationDescription,
            AskDescription = entity.AskDescription,
            MaxRowLimit = entity.MaxRowLimit,
            EnforceReadOnly = entity.EnforceReadOnly,
            EnablePiiDetection = entity.EnablePiiDetection,
            CustomPiiPatterns = ParsePiiPatterns(entity.CustomPiiPatterns, "global", entity.Id) ?? [],
            EnableSampleValueCollection = entity.EnableSampleValueCollection,
            EnableLearning = entity.EnableLearning,
            LearningAutoApproveThreshold = entity.LearningAutoApproveThreshold,
            LearningInjectionBudgetChars = entity.LearningInjectionBudgetChars,
            LearningSignalRetentionDays = entity.LearningSignalRetentionDays,
            EnableSelfConsistency = entity.EnableSelfConsistency,
            SelfConsistencyCandidateCount = entity.SelfConsistencyCandidateCount,
            EnableEvalJudge = entity.EnableEvalJudge,
            EnableSemanticRetrieval = entity.EnableSemanticRetrieval,
            ExemplarTopK = entity.ExemplarTopK,
            EnableReplayVerification = entity.EnableReplayVerification,
            LearningReplayMinFlips = entity.LearningReplayMinFlips,
            EnableContextualRetrieval = entity.EnableContextualRetrieval,
            DocChunkWindowSentences = entity.DocChunkWindowSentences,
            DocChunkOverlapSentences = entity.DocChunkOverlapSentences,
            GlossaryTopK = entity.GlossaryTopK,
            DocChunkTopK = entity.DocChunkTopK,
            EnableGoldenExemplars = entity.EnableGoldenExemplars,
            GoldenExemplarTopK = entity.GoldenExemplarTopK,
            GoldenExemplarBudgetChars = entity.GoldenExemplarBudgetChars,
            EnableValueGrounding = entity.EnableValueGrounding,
            ValueGroundingMaxProbes = entity.ValueGroundingMaxProbes,
            EnableSemanticLint = entity.EnableSemanticLint,
            SelfConsistencyMinTables = entity.SelfConsistencyMinTables,
            RetainQueryContent = entity.RetainQueryContent,
            StatementTimeoutSeconds = entity.StatementTimeoutSeconds,
            MaxResultBytes = entity.MaxResultBytes,
            MaxExplainCost = entity.MaxExplainCost,
            MaxConcurrentQueriesPerKey = entity.MaxConcurrentQueriesPerKey,
            AllowExplicitFeedbackContent = entity.AllowExplicitFeedbackContent
        };
    }

    private McpProjectSettingsData MapToProjectData(McpProjectSettings entity)
    {
        return new McpProjectSettingsData
        {
            MaxRowLimit = entity.MaxRowLimit,
            EnforceReadOnly = entity.EnforceReadOnly,
            EnablePiiDetection = entity.EnablePiiDetection,
            CustomPiiPatterns = ParsePiiPatterns(entity.CustomPiiPatterns, "project", entity.Id),
            EnableSampleValueCollection = entity.EnableSampleValueCollection,
            EnableLearning = entity.EnableLearning,
            LearningAutoApproveThreshold = entity.LearningAutoApproveThreshold,
            LearningInjectionBudgetChars = entity.LearningInjectionBudgetChars,
            LearningSignalRetentionDays = entity.LearningSignalRetentionDays,
            EnableSelfConsistency = entity.EnableSelfConsistency,
            SelfConsistencyCandidateCount = entity.SelfConsistencyCandidateCount,
            EnableEvalJudge = entity.EnableEvalJudge,
            EnableSemanticRetrieval = entity.EnableSemanticRetrieval,
            ExemplarTopK = entity.ExemplarTopK,
            EnableReplayVerification = entity.EnableReplayVerification,
            LearningReplayMinFlips = entity.LearningReplayMinFlips,
            EnableContextualRetrieval = entity.EnableContextualRetrieval,
            DocChunkWindowSentences = entity.DocChunkWindowSentences,
            DocChunkOverlapSentences = entity.DocChunkOverlapSentences,
            GlossaryTopK = entity.GlossaryTopK,
            DocChunkTopK = entity.DocChunkTopK,
            EnableGoldenExemplars = entity.EnableGoldenExemplars,
            GoldenExemplarTopK = entity.GoldenExemplarTopK,
            GoldenExemplarBudgetChars = entity.GoldenExemplarBudgetChars,
            EnableValueGrounding = entity.EnableValueGrounding,
            ValueGroundingMaxProbes = entity.ValueGroundingMaxProbes,
            EnableSemanticLint = entity.EnableSemanticLint,
            SelfConsistencyMinTables = entity.SelfConsistencyMinTables,
            RetainQueryContent = entity.RetainQueryContent,
            StatementTimeoutSeconds = entity.StatementTimeoutSeconds,
            MaxResultBytes = entity.MaxResultBytes,
            MaxExplainCost = entity.MaxExplainCost,
            MaxConcurrentQueriesPerKey = entity.MaxConcurrentQueriesPerKey,
            AllowExplicitFeedbackContent = entity.AllowExplicitFeedbackContent
        };
    }
}
