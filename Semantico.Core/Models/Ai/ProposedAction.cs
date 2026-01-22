using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.Ai;

public record ProposedAction
{
    public AiActorActionType ActionType { get; init; }
    public string Reasoning { get; init; } = null!;
    public Dictionary<string, object?> Parameters { get; init; } = new();

    /// <summary>
    /// For RefineQuery actions, the current SQL
    /// </summary>
    public string? CurrentSql { get; init; }

    /// <summary>
    /// For RefineQuery actions, the proposed new SQL
    /// </summary>
    public string? ProposedSql { get; init; }

    /// <summary>
    /// For RefineQuery actions, whether the query is locked
    /// </summary>
    public bool IsLocked { get; init; }

    /// <summary>
    /// Target query name (for display purposes)
    /// </summary>
    public string? TargetQueryName { get; init; }

    /// <summary>
    /// Target query ID (if applicable)
    /// </summary>
    public int? TargetQueryId { get; init; }
}
