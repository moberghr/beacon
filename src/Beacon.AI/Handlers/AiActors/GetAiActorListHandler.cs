using Beacon.Core.Data;
using Beacon.Core.Handlers.AiActors;
using Beacon.Core.Helpers;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class GetAiActorListHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetAiActorListQuery, PagedList<AiActorListItem>>
{
    private const int InstructionsPreviewLength = 100;

    public async Task<PagedList<AiActorListItem>> Handle(
        GetAiActorListQuery request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.AiActors.AsNoTracking();

        // The global soft-delete filter excludes archived actors by default; opt back in explicitly.
        if (request.IncludeArchived == true)
        {
            query = query.IgnoreQueryFilters();
        }

        if (request.DataSourceId.HasValue)
        {
            query = query.Where(x => x.DataSourceId == request.DataSourceId.Value);
        }

        return await query
            .Select(x =>
                new AiActorListItem
                {
                    ActorId = x.Id,
                    Name = x.Name,
                    Instructions = x.Instructions.Length > InstructionsPreviewLength
                        ? x.Instructions.Substring(0, InstructionsPreviewLength) + "..."
                        : x.Instructions,
                    DataSourceId = x.DataSourceId,
                    DataSourceName = x.DataSource != null ? x.DataSource.Name : "Unknown",
                    Status = x.Status,
                    ThinkCount = x.ThinkCount,
                    LastThinkTime = x.LastThinkTime,
                    TotalCost = x.TotalCost,
                    CreatedTime = x.CreatedTime
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdTime", tiebreaker: "actorId");
    }
}
