using MediatR;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Enums;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.Core.Handlers.Ai.AiActor;

namespace Beacon.AI.Handlers.Ai.AiActor;

internal sealed class GetAiActorListHandler : IRequestHandler<GetAiActorListQuery, GetAiActorListResult>
{
    private readonly IAiActorServiceExtended _aiActorService;
    private readonly ILogger<GetAiActorListHandler> _logger;

    public GetAiActorListHandler(
        IAiActorServiceExtended aiActorService,
        ILogger<GetAiActorListHandler> logger)
    {
        _aiActorService = aiActorService;
        _logger = logger;
    }

    public async Task<GetAiActorListResult> Handle(
        GetAiActorListQuery request,
        CancellationToken cancellationToken)
    {
        var actors = await _aiActorService.GetActorsForDataSourceAsync(
            request.DataSourceId,
            request.IncludeArchived ?? false,
            cancellationToken);

        return new GetAiActorListResult
        {
            Actors = actors.Select(a => new AiActorListItem
            {
                ActorId = a.Id,
                Name = a.Name,
                Instructions = a.Instructions.Length > 100
                    ? a.Instructions[..100] + "..."
                    : a.Instructions,
                DataSourceId = a.DataSourceId,
                DataSourceName = a.DataSource?.Name ?? "Unknown",
                Status = a.Status,
                ThinkCount = a.ThinkCount,
                LastThinkTime = a.LastThinkTime,
                TotalCost = a.TotalCost,
                CreatedTime = a.CreatedTime
            }).ToList()
        };
    }
}

