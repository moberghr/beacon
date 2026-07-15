using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

public class McpLearnedPattern : BaseEntity
{
    public int ProjectId { get; set; }
    public int DataSourceId { get; set; }
    public string SchemaName { get; set; } = null!;
    public string TableName { get; set; } = null!;
    public string? ColumnName { get; set; }

    public McpPatternType PatternType { get; set; }
    public string PatternContent { get; set; } = null!;
    public string? ExampleQuestion { get; set; }
    public string? ExampleSql { get; set; }

    public int SignalCount { get; set; }
    public double Confidence { get; set; }

    public McpPatternStatus Status { get; set; }
    public int? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime? LastRefreshedAt { get; set; }
    public DateTime? SupersededAt { get; set; }
    public DateTime? LastVerifiedAt { get; set; }
}
