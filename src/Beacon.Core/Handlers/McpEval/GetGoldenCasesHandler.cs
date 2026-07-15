using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>Lists golden eval cases, optionally scoped by project and/or data source.</summary>
internal sealed class GetGoldenCasesHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetGoldenCasesQuery, List<GoldenCaseItem>>
{
    public async Task<List<GoldenCaseItem>> Handle(GetGoldenCasesQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.McpEvalCases.AsQueryable();
        if (request.ProjectId.HasValue)
        {
            query = query.Where(x => x.ProjectId == request.ProjectId.Value);
        }

        if (request.DataSourceId.HasValue)
        {
            query = query.Where(x => x.DataSourceId == request.DataSourceId.Value);
        }

        return await query
            .OrderByDescending(x => x.CreatedTime)
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
                    CreatedTime = x.CreatedTime
                })
            .ToListAsync(cancellationToken);
    }
}

public record GetGoldenCasesQuery : IRequest<List<GoldenCaseItem>>
{
    public int? ProjectId { get; init; }
    public int? DataSourceId { get; init; }
}

public record GoldenCaseItem
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public int DataSourceId { get; init; }
    public string Question { get; init; } = "";
    public string GoldSql { get; init; } = "";
    public int? SourceSignalId { get; init; }
    public bool IsActive { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedTime { get; init; }
}
