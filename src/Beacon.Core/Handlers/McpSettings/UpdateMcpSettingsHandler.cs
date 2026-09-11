using System.Text.Json;
using System.Text.RegularExpressions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.McpSettings;

internal sealed class UpdateMcpSettingsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider,
    IOptions<McpDeploymentOptions> deploymentOptions)
    : IRequestHandler<UpdateMcpSettingsCommand>
{
    public async Task Handle(UpdateMcpSettingsCommand request, CancellationToken cancellationToken)
    {
        var data = request.Data;

        // Deployment locks win over any stored value (§1.5, R1/R2). A write that contradicts one is refused
        // before touching the DB — the API maps SettingLockedException to 409.
        var locks = deploymentOptions.Value;
        var activeLocks = McpLockPolicy.ActiveRules(locks);
        foreach (var rule in activeLocks)
        {
            if (rule.Contradicts(data))
            {
                throw SettingLockedException.For(rule.FieldName, rule.LockName);
            }
        }

        // Reject invalid custom PII regexes at write time (before touching the DB) so an unparseable
        // pattern can never be persisted and later silently skipped in the detection hot paths
        // (fail-open PII leak).
        foreach (var pattern in data.CustomPiiPatterns)
        {
            try
            {
                _ = Regex.Match(string.Empty, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Custom PII pattern is not a valid regular expression: {ex.Message}");
            }
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.McpSettings.FirstOrDefaultAsync(cancellationToken);

        if (entity == null)
        {
            entity = new Data.Entities.McpSettings();
            context.McpSettings.Add(entity);
        }

        entity.AskSystemPrompt = data.AskSystemPrompt;
        entity.GlobalInstruction = data.GlobalInstruction;
        entity.GetContextDescription = data.GetContextDescription;
        entity.SearchDescription = data.SearchDescription;
        entity.QueryDescription = data.QueryDescription;
        entity.GetDocumentationDescription = data.GetDocumentationDescription;
        entity.AskDescription = data.AskDescription;
        // The global GET hands the page lock/ceiling-RESOLVED values and the page re-sends every field, so a
        // locked or clamped field coming back equal to the resolved value is not an edit. Keep the stored value
        // there, otherwise one unrelated save bakes the deployment value into the row and lifting the lock or
        // ceiling later would no longer restore the admin's configuration. Locked fields: the incoming value is
        // necessarily the pinned one (a contradicting one was refused above), so the stored value is copied back
        // into the incoming DTO before mapping. Clamped fields: see KeepStoredWhenClamped. The project handler
        // needs neither — project rows store raw overrides and the project GET returns them raw.
        foreach (var rule in activeLocks)
        {
            rule.KeepStored(data, entity);
        }

        var ceilings = locks.Ceilings ?? new McpCeilingOptions();
        entity.MaxRowLimit = KeepStoredWhenClamped(entity.MaxRowLimit, data.MaxRowLimit, ceilings.MaxRowLimit);
        entity.EnforceReadOnly = data.EnforceReadOnly;
        entity.EnablePiiDetection = data.EnablePiiDetection;
        entity.CustomPiiPatterns = data.CustomPiiPatterns.Count > 0
            ? JsonSerializer.Serialize(data.CustomPiiPatterns)
            : null;
        entity.EnableSampleValueCollection = data.EnableSampleValueCollection;

        // Learning settings
        entity.EnableLearning = data.EnableLearning;
        entity.LearningAutoApproveThreshold = data.LearningAutoApproveThreshold;
        entity.LearningInjectionBudgetChars = data.LearningInjectionBudgetChars;
        entity.LearningSignalRetentionDays = data.LearningSignalRetentionDays;

        // Self-learning settings
        entity.EnableSelfConsistency = data.EnableSelfConsistency;
        entity.SelfConsistencyCandidateCount = data.SelfConsistencyCandidateCount;
        entity.EnableEvalJudge = data.EnableEvalJudge;
        entity.EnableSemanticRetrieval = data.EnableSemanticRetrieval;
        entity.ExemplarTopK = data.ExemplarTopK;

        // Replay-verification settings
        entity.EnableReplayVerification = data.EnableReplayVerification;
        entity.LearningReplayMinFlips = data.LearningReplayMinFlips;

        // Knowledge-base Tier 3 settings
        entity.EnableContextualRetrieval = data.EnableContextualRetrieval;
        entity.DocChunkWindowSentences = data.DocChunkWindowSentences;
        entity.DocChunkOverlapSentences = data.DocChunkOverlapSentences;
        entity.GlossaryTopK = data.GlossaryTopK;
        entity.DocChunkTopK = data.DocChunkTopK;

        // Golden-exemplar settings
        entity.EnableGoldenExemplars = data.EnableGoldenExemplars;
        entity.GoldenExemplarTopK = data.GoldenExemplarTopK;
        entity.GoldenExemplarBudgetChars = data.GoldenExemplarBudgetChars;

        // Ask-correctness grounding settings
        entity.EnableValueGrounding = data.EnableValueGrounding;
        entity.ValueGroundingMaxProbes = data.ValueGroundingMaxProbes;
        entity.EnableSemanticLint = data.EnableSemanticLint;
        entity.SelfConsistencyMinTables = data.SelfConsistencyMinTables;

        // Warehouse-engine settings (Wave 0.2)
        entity.RetainQueryContent = data.RetainQueryContent;
        entity.StatementTimeoutSeconds = KeepStoredWhenClamped(entity.StatementTimeoutSeconds, data.StatementTimeoutSeconds, ceilings.StatementTimeoutSeconds);
        entity.MaxResultBytes = KeepStoredWhenClamped(entity.MaxResultBytes, data.MaxResultBytes, ceilings.MaxResultBytes);
        entity.MaxExplainCost = KeepStoredWhenClamped(entity.MaxExplainCost, data.MaxExplainCost, ceilings.MaxExplainCost);
        entity.MaxConcurrentQueriesPerKey = KeepStoredWhenClamped(entity.MaxConcurrentQueriesPerKey, data.MaxConcurrentQueriesPerKey, ceilings.MaxConcurrentQueriesPerKey);
        entity.AllowExplicitFeedbackContent = data.AllowExplicitFeedbackContent;

        await context.SaveChangesAsync(cancellationToken);
        settingsProvider.InvalidateCache();
    }

    // A stored value above the ceiling resolves to the ceiling; the same value coming back is the page echoing
    // what it was shown, not a change. Any other incoming value is a deliberate edit and is written as-is.
    // Known limitation (review F007): a deliberate edit to EXACTLY the ceiling value while the stored value is
    // above it is indistinguishable from an echo and is not persisted; the effective value is the ceiling either
    // way. Removing the ambiguity needs the global GET to return stored + resolved values (contract change,
    // follow-up spec).
    private static int KeepStoredWhenClamped(int stored, int incoming, int? ceiling)
    {
        return ceiling is { } max && stored > max && incoming == max ? stored : incoming;
    }

    // MaxExplainCost: null means "no limit", which resolves to the ceiling when one is configured.
    private static decimal? KeepStoredWhenClamped(decimal? stored, decimal? incoming, decimal? ceiling)
    {
        var storedIsAboveCeiling = ceiling is { } max && (stored == null || stored > max);

        return storedIsAboveCeiling && incoming == ceiling ? stored : incoming;
    }
}

public record UpdateMcpSettingsCommand(McpSettingsData Data) : IRequest;
