namespace Beacon.Core.Data.Enums;

/// <summary>The kind of knowledge item an <c>ask</c> prompt carried, recorded on <see cref="Entities.McpSignalContextItem"/>.</summary>
public enum McpKnowledgeItemKind
{
    GoldenCase = 1,
    LearnedPattern = 2,
    GlossaryTerm = 3
}
