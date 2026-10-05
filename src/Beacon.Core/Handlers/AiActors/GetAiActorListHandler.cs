using Beacon.Core.Helpers;
using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;



namespace Beacon.Core.Handlers.AiActors;

/// <summary>Newest first unless <c>sort</c> says otherwise; optionally one data source, optionally archived too.</summary>
public record GetAiActorListQuery : ListRequest, IRequest<PagedList<AiActorListItem>>
{
    public int? DataSourceId { get; init; }

    public bool? IncludeArchived { get; init; }
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
