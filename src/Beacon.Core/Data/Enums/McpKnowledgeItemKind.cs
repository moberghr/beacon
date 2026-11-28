namespace Beacon.Core.Data.Enums;

/// <summary>The kind of knowledge item an <c>ask</c> prompt carried, recorded on <see cref="Entities.McpSignalContextItem"/>.</summary>
public enum McpKnowledgeItemKind
{
    GoldenCase = 1,
    LearnedPattern = 2,
    GlossaryTerm = 3,

    /// <summary>A table description Beacon wrote (<see cref="Entities.McpDescriptionItem"/> with an empty column name).</summary>
    TableDescription = 4,

    /// <summary>A column description Beacon wrote (<see cref="Entities.McpDescriptionItem"/> with its column name).</summary>
    ColumnDescription = 5,

    /// <summary>A documentation chunk (<see cref="Entities.McpDocChunk"/>); a rebuilt chunk gets a new id.</summary>
    DocChunk = 6
}
