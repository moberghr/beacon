using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services.Ai.AiActor;

namespace Semantico.Core.Handlers.Ai.AiActor;

internal sealed class GetAiActorListHandler : IRequestHandler<GetAiActorListQuery, GetAiActorListResult>
{
    private readonly IAiActorService _aiActorService;
    private readonly ILogger<GetAiActorListHandler> _logger;

    public GetAiActorListHandler(
        IAiActorService aiActorService,
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

public record GetAiActorListQuery : IRequest<GetAiActorListResult>
{
    public required int DataSourceId { get; init; }
    public bool? IncludeArchived { get; init; }
}

public record GetAiActorListResult
{
    public List<AiActorListItem> Actors { get; init; } = new();
}

public record AiActorListItem
{
    public int ActorId { get; init; }
    public string Name { get; init; } = null!;
    public string Instructions { get; init; } = null!;
    public int DataSourceId { get; init; }
    public string DataSourceName { get; init; } = null!;
    public AiActorStatus Status { get; init; }
    public int ThinkCount { get; init; }
    public DateTime? LastThinkTime { get; init; }
    public decimal TotalCost { get; init; }
    public DateTime CreatedTime { get; init; }
}
