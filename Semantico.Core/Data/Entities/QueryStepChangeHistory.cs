using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

/// <summary>
/// Tracks changes made to a QueryStep's SQL over time with attribution.
/// </summary>
public class QueryStepChangeHistory : BaseEntity
{
    /// <summary>
    /// The query step that was modified
    /// </summary>
    public int QueryStepId { get; set; }

    /// <summary>
    /// The AI Actor that made this change (null if user-made)
    /// </summary>
    public int? AiActorId { get; set; }

    /// <summary>
    /// The specific AI Actor execution that made this change
    /// </summary>
    public int? AiActorExecutionId { get; set; }

    /// <summary>
    /// The AI Actor plan that this change was part of
    /// </summary>
    public int? AiActorPlanId { get; set; }

    /// <summary>
    /// User ID who made this change (null if AI-made)
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// SQL query before the change
    /// </summary>
    public string PreviousSql { get; set; } = null!;

    /// <summary>
    /// SQL query after the change
    /// </summary>
    public string NewSql { get; set; } = null!;

    /// <summary>
    /// Explanation of why the change was made
    /// </summary>
    public string? ChangeReason { get; set; }

    /// <summary>
    /// Source of the change (User, AiActor, Import)
    /// </summary>
    public ChangeSource ChangeSource { get; set; }

    /// <summary>
    /// When the change was made
    /// </summary>
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public QueryStep QueryStep { get; set; } = null!;
    public AiActor? AiActor { get; set; }
    public AiActorExecution? AiActorExecution { get; set; }
    public AiActorPlan? AiActorPlan { get; set; }
}
