using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.Metadata;

public record DatabaseMetadataSnapshot(
    int DataSourceId,
    DatabaseEngineType? DatabaseEngineType,
    IReadOnlyList<TableMetadataDto> Tables,
    DateTime RefreshedAt
);

public record TableMetadataDto(
    string SchemaName,
    string TableName,
    IReadOnlyList<ColumnMetadataDto> Columns,
    IReadOnlyList<IndexMetadataDto> Indexes,
    string? Description
);

public record ColumnMetadataDto(
    string ColumnName,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey,
    bool IsForeignKey,
    int OrdinalPosition,
    string? ForeignKeyTable,
    string? ForeignKeyColumn,
    string? DefaultValue,
    int? MaxLength,
    string? Description
);

public record IndexMetadataDto(
    string IndexName,
    bool IsUnique,
    bool IsPrimaryKey,
    string[] Columns
);
