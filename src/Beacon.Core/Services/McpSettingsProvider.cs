using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;

namespace Beacon.Core.Services;

public interface IMcpSettingsProvider
{
    Task<McpSettingsData> GetSettingsAsync(CancellationToken ct = default);
    void InvalidateCache();
}

internal sealed class McpSettingsProvider(
    IDbContextFactory<BeaconContext> contextFactory,
    IMemoryCache cache,
    ILogger<McpSettingsProvider> logger) : IMcpSettingsProvider
{
    private const string CacheKey = "McpSettings";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public async Task<McpSettingsData> GetSettingsAsync(CancellationToken ct = default)
    {
        return await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var entity = await context.McpSettings.FirstOrDefaultAsync(ct);

            return entity != null ? MapToData(entity) : new McpSettingsData();
        }) ?? new McpSettingsData();
    }

    public void InvalidateCache()
    {
        cache.Remove(CacheKey);
    }

    private McpSettingsData MapToData(McpSettings entity)
    {
        List<string> piiPatterns = [];
        if (!string.IsNullOrWhiteSpace(entity.CustomPiiPatterns))
        {
            try
            {
                piiPatterns = JsonSerializer.Deserialize<List<string>>(entity.CustomPiiPatterns) ?? [];
            }
            catch (JsonException ex)
            {
                // A corrupt row silently drops ALL custom PII patterns for every consumer; make the
                // misconfiguration discoverable rather than degrading protection invisibly.
                logger.LogError(ex, "Failed to deserialize CustomPiiPatterns for MCP settings row {SettingsId}; custom PII patterns are disabled until fixed.", entity.Id);
            }
        }

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
            CustomPiiPatterns = piiPatterns,
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
            DocChunkTopK = entity.DocChunkTopK
        };
    }
}
