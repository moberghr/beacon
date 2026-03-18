using Semantico.Core.Data.Enums;

namespace Semantico.AI.Services.Knowledge;

public record TableKnowledge
{
    public int DataSourceId { get; init; }
    public required string DataSourceName { get; init; }
    public required string SchemaName { get; init; }
    public required string TableName { get; init; }

    // Schema info
    public List<ColumnInfo> Columns { get; init; } = new();
    public List<IndexInfo> Indexes { get; init; } = new();
    public List<RelationshipInfo> Relationships { get; init; } = new();

    // AI-generated documentation
    public string? Description { get; init; }
    public string? BusinessPurpose { get; init; }

    // Code references (from GitHub scanner)
    public List<CodeReferenceInfo> CodeReferences { get; init; } = new();

    // Data quality
    public double? QualityScore { get; init; }
    public string? QualityTrend { get; init; }
    public List<QualityRuleInfo> QualityRules { get; init; } = new();

    // Usage patterns
    public int QueryCount { get; init; }
    public DateTime? LastQueried { get; init; }
}

public record ColumnInfo(string Name, string DataType, bool IsNullable, bool IsPrimaryKey, string? Description, string? ForeignKeyTable, string? ForeignKeyColumn);

public record IndexInfo(string Name, string Columns, bool IsUnique);

public record RelationshipInfo(string Type, string RelatedSchema, string RelatedTable, string ForeignKeyColumn, string ReferencedColumn);

public record CodeReferenceInfo(string FilePath, int? LineNumber, CodeReferenceType Type, string? ClassName, string? MethodName, string? Snippet);

public record QualityRuleInfo(string RuleType, string? ColumnName, bool Passed, string? Details);

public record DataSourceKnowledge
{
    public int DataSourceId { get; init; }
    public required string Name { get; init; }
    public DataSourceType DataSourceType { get; init; }
    public string? DatabaseEngine { get; init; }
    public int TableCount { get; init; }
    public double? OverallQualityScore { get; init; }
    public int CodeReferenceCount { get; init; }
    public bool HasDocumentation { get; init; }
    public List<SchemaOverview> Schemas { get; init; } = new();
}

public record SchemaOverview(string SchemaName, int TableCount, double? AvgQualityScore);

public record SearchResult
{
    public required string Type { get; init; } // "table", "column", "documentation"
    public int DataSourceId { get; init; }
    public required string DataSourceName { get; init; }
    public required string SchemaName { get; init; }
    public required string TableName { get; init; }
    public string? ColumnName { get; init; }
    public string? Description { get; init; }
    public double Relevance { get; init; }
}

public record LineageInfo
{
    public required string SchemaName { get; init; }
    public required string TableName { get; init; }
    public List<LineageNode> WrittenBy { get; init; } = new();
    public List<LineageNode> ReadBy { get; init; } = new();
    public List<LineageNode> RelatedTables { get; init; } = new();
}

public record LineageNode(string Type, string Name, string? Detail);

public record SmartSchemaContext
{
    public required string FullContext { get; init; }
    public bool UsedSmartRetrieval { get; init; }
    public List<string> RelevantTables { get; init; } = [];
    public int TotalTableCount { get; init; }
}
