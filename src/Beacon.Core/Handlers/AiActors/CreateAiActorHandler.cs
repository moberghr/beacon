using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Entities;
using Beacon.Core.Authorization;



namespace Beacon.Core.Handlers.AiActors;

/// <summary>
/// Creates an AI actor whose creator is the signed-in caller. Its creator or an Admin may later pause, resume, archive,
/// refine or run it.
/// </summary>
public record CreateAiActorCommand : IRequest<CreateAiActorResult>, IProjectAuthorizedRequest
{
    public required string Name { get; init; }
    public required string Instructions { get; init; }
    public required int DataSourceId { get; init; }
    public string? AdditionalContext { get; init; }
    public int? MaxQueries { get; init; }
    public int? MaxSubscriptionsPerQuery { get; init; }
    [ProjectScopeExempt("recipients are project-less; attaching them needs Write on the actor's data source, checked")]
    public List<int>? DefaultRecipientIds { get; init; }
    public bool? ActivateImmediately { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.DataSource(DataSourceId), ProjectAction.Write)
    ];
}

public record CreateAiActorResult
{
    public int ActorId { get; init; }
    public string Name { get; init; } = null!;
    public AiActorStatus Status { get; init; }
    public int DataSourceId { get; init; }
    public DateTime CreatedTime { get; init; }
}
