namespace Semantico.Core.Services.Ai.DocumentationAgent.Models;

#region GetTableList Results

public class TableListResult
{
    public List<TableSummary> Tables { get; set; } = [];
    public int TotalCount { get; set; }
    public DateTime RefreshedAt { get; set; }
}

public class TableSummary
{
    public string SchemaName { get; set; } = null!;
    public string TableName { get; set; } = null!;
    public int ColumnCount { get; set; }
    public bool HasPrimaryKey { get; set; }
    public int ForeignKeyCount { get; set; }
    public int IndexCount { get; set; }
    public string? Description { get; set; }
}

#endregion

#region GetTableMetadata Results

public class TableMetadataResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SchemaName { get; set; }
    public string? TableName { get; set; }
    public string? Description { get; set; }
    public List<ColumnInfo> Columns { get; set; } = [];
    public List<IndexInfo> Indexes { get; set; } = [];
}

public class ColumnInfo
{
    public string Name { get; set; } = null!;
    public string DataType { get; set; } = null!;
    public bool IsNullable { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsForeignKey { get; set; }
    public string? ForeignKeyTable { get; set; }
    public string? ForeignKeyColumn { get; set; }
    public string? DefaultValue { get; set; }
    public int? MaxLength { get; set; }
    public string? Description { get; set; }
    public int OrdinalPosition { get; set; }
}

public class IndexInfo
{
    public string Name { get; set; } = null!;
    public bool IsUnique { get; set; }
    public bool IsPrimaryKey { get; set; }
    public List<string> Columns { get; set; } = [];
}

#endregion

#region GetRelationships Results

public class RelationshipsResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? TableName { get; set; }
    public List<ForeignKeyReference> OutgoingReferences { get; set; } = [];
    public List<ForeignKeyReference> IncomingReferences { get; set; } = [];
}

public class ForeignKeyReference
{
    public string? SourceTable { get; set; }
    public string SourceColumn { get; set; } = null!;
    public string? ReferencedTable { get; set; }
    public string? ReferencedColumn { get; set; }
}

#endregion

#region QuerySampleData Results

public class SampleDataResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public bool SampleDataDisabled { get; set; }
    public string? TableName { get; set; }
    public int RowCount { get; set; }
    public List<string> ColumnNames { get; set; } = [];
    public List<Dictionary<string, string?>> SampleRows { get; set; } = [];
}

#endregion

#region SaveDocumentationSection Results

public class SaveSectionResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int SectionId { get; set; }
}

#endregion

#region GetExistingSections Results

public class GetSectionsResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<SectionSummary> Sections { get; set; } = [];
    public int TotalCount { get; set; }
}

public class SectionSummary
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string SectionType { get; set; } = null!;
    public string? TableName { get; set; }
    public int SortOrder { get; set; }
    public int ContentLength { get; set; }
    public bool IsUserEdited { get; set; }
}

#endregion

#region UpdateProgress Results

public class UpdateProgressResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

#endregion
