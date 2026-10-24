using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>
/// The paged form of <see cref="GetGoldenCasesQuery"/>: golden eval cases, newest first unless the request names a sort,
/// optionally scoped by project and/or data source. Each case carries the same attribution and withheld flags as the
/// unpaged list.
/// </summary>
internal sealed class ListGoldenCasesHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<ListGoldenCasesQuery, PagedList<GoldenCaseItem>>
{
    public async Task<PagedList<GoldenCaseItem>> Handle(ListGoldenCasesQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var page = await context.McpEvalCases
            .WhereIf(request.Id.HasValue, x => x.Id == request.Id!.Value)
            .WhereIf(request.ProjectId.HasValue, x => x.ProjectId == request.ProjectId!.Value)
            .WhereIf(request.DataSourceId.HasValue, x => x.DataSourceId == request.DataSourceId!.Value)
            .Select(x =>
                new GoldenCaseItem
                {
                    Id = x.Id,
                    ProjectId = x.ProjectId,
                    DataSourceId = x.DataSourceId,
                    Question = x.Question,
                    GoldSql = x.GoldSql,
                    SourceSignalId = x.SourceSignalId,
                    IsActive = x.IsActive,
                    Notes = x.Notes,
                    CreatedTime = x.CreatedTime,
                    PromotedByUserId = x.PromotedByUserId,
                    PromotedByName = context.Users
                        .Where(y => y.Id == x.PromotedByUserId)
                        .Select(UserLabel.OfUser)
                        .FirstOrDefault(),
                    ModifiedByUserId = x.ModifiedByUserId,
                    ModifiedByName = context.Users
                        .Where(y => y.Id == x.ModifiedByUserId)
                        .Select(UserLabel.OfUser)
                        .FirstOrDefault()
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdTime");

        var dialects = await GroundingDialects.OfAsync(
            context,
            page.Items
                .Select(x => x.DataSourceId)
                .Distinct()
                .ToList(),
            cancellationToken);

        return page.Map(x => x with
        {
            GoldSqlWithheld = GroundingDialects.Withholds(x.GoldSql, dialects.GetValueOrDefault(x.DataSourceId)),
            QuestionWithheld = GroundingText.HasHiddenCharacters(x.Question)
        });
    }
}

[ProjectScopeExempt("admin-gated curation (the eval harness)", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record ListGoldenCasesQuery : ListRequest, IRequest<PagedList<GoldenCaseItem>>
{
    public int? Id { get; init; }
    public int? ProjectId { get; init; }
    public int? DataSourceId { get; init; }
}
