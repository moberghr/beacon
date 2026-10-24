using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Authorization;
using Beacon.Core.Helpers;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>Returns the per-case outcomes of a single eval run.</summary>
internal sealed class GetEvalResultsHandler(IDbContextFactory<BeaconContext> contextFactory,
    LegacyListTruncation? truncation = null,
    ILogger<GetEvalResultsHandler>? logger = null)
    : IRequestHandler<GetEvalResultsQuery, List<EvalResultItem>>
{
    public async Task<List<EvalResultItem>> Handle(GetEvalResultsQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var items = await context.McpEvalResults
            .Where(x => x.EvalRunId == request.RunId)
            .OrderBy(x => x.EvalCaseId)
            .Take(ListRequest.LegacyUnpagedCap + 1)
            .Select(x =>
                new EvalResultItem
                {
                    Id = x.Id,
                    EvalRunId = x.EvalRunId,
                    EvalCaseId = x.EvalCaseId,
                    GeneratedSql = x.GeneratedSql,
                    Passed = x.Passed,
                    FailureTag = x.FailureTag,
                    ExecutionError = x.ExecutionError,
                    JudgeUsed = x.JudgeUsed,
                    JudgeVerdict = x.JudgeVerdict,
                    ResultRowCount = x.ResultRowCount,
                    ExecutionTimeMs = x.ExecutionTimeMs
                })
            .ToListAsync(cancellationToken);

        return LegacyListTruncation.Apply(items, truncation, logger, "eval results");
    }
}

[ProjectScopeExempt("admin-gated curation (the eval harness)", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record GetEvalResultsQuery(int RunId) : IRequest<List<EvalResultItem>>;

public record EvalResultItem
{
    public int Id { get; init; }
    public int EvalRunId { get; init; }
    public int EvalCaseId { get; init; }
    public string? GeneratedSql { get; init; }
    public bool Passed { get; init; }
    public McpEvalFailureTag FailureTag { get; init; }
    public string? ExecutionError { get; init; }
    public bool JudgeUsed { get; init; }
    public string? JudgeVerdict { get; init; }
    public int? ResultRowCount { get; init; }
    public int ExecutionTimeMs { get; init; }
}
