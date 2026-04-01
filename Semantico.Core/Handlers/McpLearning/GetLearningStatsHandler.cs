using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Handlers.McpLearning;

internal sealed class GetLearningStatsHandler(IDbContextFactory<SemanticoContext> contextFactory)
    : IRequestHandler<GetLearningStatsQuery, LearningStatsResult>
{
    public async Task<LearningStatsResult> Handle(GetLearningStatsQuery request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var last7Days = now.AddDays(-7);
        var last30Days = now.AddDays(-30);

        // Run signal stats, pattern stats, and patch stats in parallel using separate contexts
        var signalStatsTask = GetSignalStatsAsync(request.ProjectId, last7Days, last30Days, cancellationToken);
        var patternStatsTask = GetPatternStatsAsync(request.ProjectId, cancellationToken);
        var patchStatsTask = GetPatchStatsAsync(request.ProjectId, cancellationToken);
        var problemTablesTask = GetProblemTablesAsync(request.ProjectId, cancellationToken);

        await Task.WhenAll(signalStatsTask, patternStatsTask, patchStatsTask, problemTablesTask);

        var (totalSignals, signals7d, signals30d, successRate) = signalStatsTask.Result;
        var (patternsApproved, patternsPending, patternsRejected) = patternStatsTask.Result;
        var (patchesApplied, patchesProposed) = patchStatsTask.Result;

        return new LearningStatsResult
        {
            TotalSignals = totalSignals,
            Signals7d = signals7d,
            Signals30d = signals30d,
            SuccessRate = successRate,
            PatternsApproved = patternsApproved,
            PatternsPending = patternsPending,
            PatternsRejected = patternsRejected,
            PatchesApplied = patchesApplied,
            PatchesProposed = patchesProposed,
            ProblemTables = problemTablesTask.Result
        };
    }

    private async Task<(int Total, int Last7d, int Last30d, double SuccessRate)> GetSignalStatsAsync(
        int? projectId, DateTime last7Days, DateTime last30Days, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var query = context.McpQuerySignals.AsQueryable();
        if (projectId.HasValue) query = query.Where(s => s.ProjectId == projectId.Value);

        var total = await query.CountAsync(ct);
        var last7d = await query.CountAsync(s => s.CreatedTime >= last7Days, ct);
        var last30d = await query.CountAsync(s => s.CreatedTime >= last30Days, ct);
        var successful = await query.CountAsync(s => s.IsSuccessful, ct);
        var rate = total > 0 ? (double)successful / total : 0;

        return (total, last7d, last30d, rate);
    }

    private async Task<(int Approved, int Pending, int Rejected)> GetPatternStatsAsync(int? projectId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var query = context.McpLearnedPatterns.AsQueryable();
        if (projectId.HasValue) query = query.Where(p => p.ProjectId == projectId.Value);

        var approved = await query.CountAsync(p => p.Status == McpPatternStatus.Approved || p.Status == McpPatternStatus.AutoApproved, ct);
        var pending = await query.CountAsync(p => p.Status == McpPatternStatus.Pending, ct);
        var rejected = await query.CountAsync(p => p.Status == McpPatternStatus.Rejected, ct);

        return (approved, pending, rejected);
    }

    private async Task<(int Applied, int Proposed)> GetPatchStatsAsync(int? projectId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var query = context.McpDocumentationPatches.AsQueryable();
        if (projectId.HasValue) query = query.Where(p => p.ProjectId == projectId.Value);

        var applied = await query.CountAsync(p => p.Status == McpDocPatchStatus.Applied, ct);
        var proposed = await query.CountAsync(p => p.Status == McpDocPatchStatus.Proposed, ct);

        return (applied, proposed);
    }

    private async Task<List<ProblemTableEntry>> GetProblemTablesAsync(int? projectId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var query = context.McpQuerySignals.AsQueryable();
        if (projectId.HasValue) query = query.Where(s => s.ProjectId == projectId.Value);

        var problemTables = await query
            .Where(s => s.TablesUsed != null)
            .GroupBy(s => s.TablesUsed!)
            .Select(g => new
            {
                Tables = g.Key,
                Total = g.Count(),
                Errors = g.Count(s => !s.IsSuccessful)
            })
            .Where(g => g.Total >= 3)
            .OrderByDescending(g => (double)g.Errors / g.Total)
            .Take(10)
            .ToListAsync(ct);

        return problemTables.Select(t => new ProblemTableEntry
        {
            TablesUsed = t.Tables,
            TotalQueries = t.Total,
            ErrorCount = t.Errors,
            ErrorRate = (double)t.Errors / t.Total
        }).ToList();
    }
}

public record GetLearningStatsQuery : IRequest<LearningStatsResult>
{
    public int? ProjectId { get; init; }
}

public record LearningStatsResult
{
    public int TotalSignals { get; init; }
    public int Signals7d { get; init; }
    public int Signals30d { get; init; }
    public double SuccessRate { get; init; }
    public int PatternsApproved { get; init; }
    public int PatternsPending { get; init; }
    public int PatternsRejected { get; init; }
    public int PatchesApplied { get; init; }
    public int PatchesProposed { get; init; }
    public List<ProblemTableEntry> ProblemTables { get; init; } = [];
}

public record ProblemTableEntry
{
    public string TablesUsed { get; init; } = "";
    public int TotalQueries { get; init; }
    public int ErrorCount { get; init; }
    public double ErrorRate { get; init; }
}
