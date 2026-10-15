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

    /// <summary>
    /// The SHA-256 (lowercase hex) of what a reviewer decides on — <see cref="PatternContent"/>, <see cref="ExampleQuestion"/>
    /// and <see cref="ExampleSql"/>, as <c>GroundingContentHash.OfPattern</c> hashes them — written with every change to them,
    /// so a decision's and an auto-approval's conditional writes compare digests, exactly under any collation. Null for a
    /// pattern stored before Beacon kept it (the first decision or auto-approval stores it).
    /// </summary>
    public string? ContentHash { get; set; }

    public int SignalCount { get; set; }
    public double Confidence { get; set; }

    public McpPatternStatus Status { get; set; }
    public int? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime? LastRefreshedAt { get; set; }
    public DateTime? SupersededAt { get; set; }
    public DateTime? LastVerifiedAt { get; set; }
}
