using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Metadata;

namespace Semantico.Core.Models.Providers;

public class DataSourceMetadata
{
    public DataSourceType Type { get; set; }

    /// <summary>
    /// For database types: table and column information
    /// </summary>
    public List<TableMetadataDto>? Tables { get; set; }

    /// <summary>
    /// For CloudWatch Logs: discovered fields from sample queries
    /// </summary>
    public List<LogFieldMetadata>? LogFields { get; set; }

    /// <summary>
    /// For CloudWatch Metrics: available metrics and dimensions
    /// </summary>
    public List<MetricMetadata>? Metrics { get; set; }

    public DateTime LastRefreshed { get; set; }
}

public class LogFieldMetadata
{
    public string FieldName { get; set; } = null!;
    public string DataType { get; set; } = null!; // "string", "number", "timestamp"
    public int SampleCount { get; set; }
    public List<string>? SampleValues { get; set; }
}

public class MetricMetadata
{
    public string MetricName { get; set; } = null!;
    public string Namespace { get; set; } = null!;
    public List<string> Dimensions { get; set; } = new();
    public string Unit { get; set; } = null!;
}
