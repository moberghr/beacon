using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Authorization;
using Beacon.Core.Helpers;

namespace Beacon.Core.Handlers.McpGlossary;

/// <summary>
/// Lists glossary terms for a project, optionally scoped to a data source and optionally including
/// deactivated terms (admin management view). Active-only by default so the CRUD list mirrors what
/// injection sees.
/// </summary>
internal sealed class GetGlossaryTermsHandler(IDbContextFactory<BeaconContext> contextFactory,
    LegacyListTruncation? truncation = null,
    ILogger<GetGlossaryTermsHandler>? logger = null)
    : IRequestHandler<GetGlossaryTermsQuery, List<GlossaryTermItem>>
{
    public async Task<List<GlossaryTermItem>> Handle(GetGlossaryTermsQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.McpGlossaryTerms
            .Where(x => x.ProjectId == request.ProjectId);

        if (request.DataSourceId.HasValue)
        {
            query = query.Where(x => x.DataSourceId == request.DataSourceId.Value);
        }

        if (!request.IncludeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        var items = await query
            .OrderBy(x => x.Term)
            .Take(ListRequest.LegacyUnpagedCap + 1)
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
            .ToListAsync(cancellationToken);

        return LegacyListTruncation.Apply(items, truncation, logger, "glossary");
    }
}

[ProjectScopeExempt("admin-gated curation (the glossary)", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record GetGlossaryTermsQuery : IRequest<List<GlossaryTermItem>>
{
    public required int ProjectId { get; init; }
    public int? DataSourceId { get; init; }
    public bool IncludeInactive { get; init; }
}

public record GlossaryTermItem
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public int? DataSourceId { get; init; }
    public string Term { get; init; } = "";
    public string? Synonyms { get; init; }
    public string Definition { get; init; } = "";
    public string? TargetSchema { get; init; }
    public string? TargetTable { get; init; }
    public string? TargetColumn { get; init; }
    public string? MetricExpression { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedTime { get; init; }
}
