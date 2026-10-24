using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Helpers;

namespace Beacon.Core.Handlers.McpGlossary;

/// <summary>
/// The paged form of <see cref="GetGlossaryTermsQuery"/>: a project's glossary terms, sorted by term unless the request
/// names a sort, optionally scoped to a data source, matching a search on the term, and including deactivated terms only
/// when asked.
/// </summary>
internal sealed class ListGlossaryTermsHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<ListGlossaryTermsQuery, PagedList<GlossaryTermItem>>
{
    public async Task<PagedList<GlossaryTermItem>> Handle(ListGlossaryTermsQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var search = ListRequest.ClampSearch(request.Search);

        return await context.McpGlossaryTerms
            .Where(x => x.ProjectId == request.ProjectId)
            .WhereIf(request.DataSourceId.HasValue, x => x.DataSourceId == request.DataSourceId!.Value)
            .WhereIf(request.IncludeInactive != true, x => x.IsActive)
            .WhereIf(search != null, x => x.Term.Contains(search!))
            .Select(x =>
                new GlossaryTermItem
                {
                    Id = x.Id,
                    ProjectId = x.ProjectId,
                    DataSourceId = x.DataSourceId,
                    Term = x.Term,
                    Synonyms = x.Synonyms,
                    Definition = x.Definition,
                    TargetSchema = x.TargetSchema,
                    TargetTable = x.TargetTable,
                    TargetColumn = x.TargetColumn,
                    MetricExpression = x.MetricExpression,
                    IsActive = x.IsActive,
                    CreatedTime = x.CreatedTime
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "term");
    }
}

[ProjectScopeExempt("admin-gated curation (the glossary)", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record ListGlossaryTermsQuery : ListRequest, IRequest<PagedList<GlossaryTermItem>>
{
    public required int ProjectId { get; init; }
    public int? DataSourceId { get; init; }
    public bool? IncludeInactive { get; init; }
    public string? Search { get; init; }
}
