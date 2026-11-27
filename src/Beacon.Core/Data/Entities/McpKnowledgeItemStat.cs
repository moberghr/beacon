using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// How the <c>ask</c> signals that carried one knowledge item in one project went, recomputed by the learning job from the
/// signals in the retention window. Ids, counts and scores only. No foreign key: <see cref="ItemId"/> points into a
/// different table by <see cref="Kind"/>.
/// </summary>
public class McpKnowledgeItemStat : BaseEntity
{
    public int ProjectId { get; set; }

    public McpKnowledgeItemKind Kind { get; set; }

    /// <summary>The item's id in its own table (<c>McpEvalCases</c>, <c>McpLearnedPatterns</c> or <c>McpGlossaryTerms</c>, by <see cref="Kind"/>).</summary>
    public int ItemId { get; set; }

    public double HelpfulScore { get; set; }

    public double HarmfulScore { get; set; }

    /// <summary>The ask signals in the window that carried the item.</summary>
    public int SignalCount { get; set; }

    /// <summary>The distinct callers behind those signals.</summary>
    public int CallerCount { get; set; }

    public DateTime ComputedAt { get; set; }
}
