using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Helpers;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>The paged form of <see cref="GetEvalResultsQuery"/>: the per-case outcomes of one eval run, by case id unless the request names a sort.</summary>
internal sealed class ListEvalResultsHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<ListEvalResultsQuery, PagedList<EvalResultItem>>
{
    public async Task<PagedList<EvalResultItem>> Handle(ListEvalResultsQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.McpEvalResults
            .Where(x => x.EvalRunId == request.RunId)
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
            .ToPagedListAsync(request, cancellationToken, defaultSort: "evalCaseId");
    }
}

[ProjectScopeExempt("admin-gated curation (the eval harness)", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record ListEvalResultsQuery : ListRequest, IRequest<PagedList<EvalResultItem>>
{
    public required int RunId { get; init; }
}
