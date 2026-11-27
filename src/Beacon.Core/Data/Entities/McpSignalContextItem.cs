using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// One knowledge item (a golden case, a learned pattern or a glossary term) the prompt behind an <c>ask</c> signal carried,
/// as a child of its <see cref="McpQuerySignal"/>, deleted with it. Ids and ranks only: the item's text stays with the item.
/// </summary>
public class McpSignalContextItem : BaseEntity
{
    public int SignalId { get; set; }

    public McpKnowledgeItemKind Kind { get; set; }

    /// <summary>The item's id in its own table (<c>McpEvalCases</c>, <c>McpLearnedPatterns</c> or <c>McpGlossaryTerms</c>, by <see cref="Kind"/>).</summary>
    public int ItemId { get; set; }

    /// <summary>The item's place in the prompt's block of its kind, in rendered order.</summary>
    public int Rank { get; set; }

    public McpQuerySignal Signal { get; set; } = null!;
}
