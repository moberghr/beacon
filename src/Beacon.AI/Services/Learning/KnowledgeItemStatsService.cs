using Microsoft.EntityFrameworkCore;

namespace Beacon.AI.Services.Learning;

/// <summary>
/// Recomputes a project's knowledge item counters from its <c>ask</c> signals inside the learning-signal retention window,
/// with <see cref="KnowledgeItemStatsCalculator"/>. Only the project's own signals, and of a follow-up re-run only the
/// project's own, count (§1.12); nothing but ids, enums, flags and the caller key is read (§1.11). One save per project
/// (§5.7).
/// </summary>
internal sealed class KnowledgeItemStatsService(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider,
    TimeProvider timeProvider) : IKnowledgeItemStatsService
{
    public async Task RecomputeAsync(int projectId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now.AddDays(-await RetentionDaysAsync(projectId, ct));

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var signals = await context.McpQuerySignals
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.Tool == "ask")
            .Where(x => x.CreatedTime >= cutoff)
            .Where(x => x.ContextItems.Any())
            .Select(x =>
                new KnowledgeItemSignal
                {
                    SignalId = x.Id,
                    CallerHash = x.CallerHash,
                    UserId = x.UserId,
                    Verdict = x.UserVerdict,
                    FromCurator = x.FeedbackFromCurator,
                    Succeeded = x.IsSuccessful,
                    HasEditedRerun = context.McpQuerySignals
                        .Where(y => y.ParentSignalId == x.Id)
                        .Where(y => y.ProjectId == projectId)
                        .Where(y => y.Tool == "query" || y.Tool == "dry_run")
                        .Where(y => y.IsEditedRerun)
                        .Any(),
                    Items = x.ContextItems
                        .Select(y =>
                            new KnowledgeItemKey(y.Kind, y.ItemId))
                        .ToList()
                })
            .ToListAsync(ct);

        var scores = KnowledgeItemStatsCalculator.Compute(signals);
        var scoredItems = scores
            .Select(x => x.Item)
            .ToHashSet();

        var storedRows = await context.McpKnowledgeItemStats
            .Where(x => x.ProjectId == projectId)
            .ToListAsync(ct);
        var storedByItem = storedRows.ToDictionary(x => new KnowledgeItemKey(x.Kind, x.ItemId));

        foreach (var score in scores)
        {
            if (storedByItem.TryGetValue(score.Item, out var row))
            {
                row.HelpfulScore = score.HelpfulScore;
                row.HarmfulScore = score.HarmfulScore;
                row.SignalCount = score.SignalCount;
                row.CallerCount = score.CallerCount;
                row.ComputedAt = now;
            }
        }

        var staleRows = storedRows
            .Where(x => !scoredItems.Contains(new KnowledgeItemKey(x.Kind, x.ItemId)))
            .ToList();

        var newRows = scores
            .Where(x => !storedByItem.ContainsKey(x.Item))
            .Select(x =>
                new McpKnowledgeItemStat
                {
                    ProjectId = projectId,
                    Kind = x.Item.Kind,
                    ItemId = x.Item.ItemId,
                    HelpfulScore = x.HelpfulScore,
                    HarmfulScore = x.HarmfulScore,
                    SignalCount = x.SignalCount,
                    CallerCount = x.CallerCount,
                    ComputedAt = now
                })
            .ToList();

        context.McpKnowledgeItemStats.RemoveRange(staleRows);
        foreach (var row in newRows)
        {
            context.McpKnowledgeItemStats.Add(row);
        }

        await context.SaveChangesAsync(ct);
    }

    // The window the learning job mines in (McpLearningAggregationService): the project's effective window, a stored window
    // below one day falling back to the global one, and that to the default.
    private async Task<int> RetentionDaysAsync(int projectId, CancellationToken ct)
    {
        var globalSettings = await settingsProvider.GetSettingsAsync(ct);
        var projectSettings = await settingsProvider.GetEffectiveSettingsAsync(projectId, ct);
        var globalDays = McpLearningAggregationService.RetentionDays(
            globalSettings.LearningSignalRetentionDays,
            McpSettingsData.DefaultLearningSignalRetentionDays);

        return McpLearningAggregationService.RetentionDays(projectSettings.LearningSignalRetentionDays, globalDays);
    }
}
