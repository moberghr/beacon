using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Metadata;

namespace Beacon.Core.Models.Providers;

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

    /// <summary>
    /// For API types: discovered endpoints from OpenAPI spec
    /// </summary>
    public List<ApiEndpointMetadata>? Endpoints { get; set; }

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

public class ApiEndpointMetadata
{
    public string Method { get; set; } = null!;
    public string Path { get; set; } = null!;
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? Tag { get; set; }
    public List<ApiParameterMetadata> Parameters { get; set; } = new();
    public List<ApiResponseFieldMetadata> ResponseFields { get; set; } = new();
}

public class ApiParameterMetadata
{
    public string Name { get; set; } = null!;
    public string In { get; set; } = null!; // "query", "path", "header"
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string? Description { get; set; }
}

public class ApiResponseFieldMetadata
{
    public string Name { get; set; } = null!;
    public string Type { get; set; } = "string";
    public string? Description { get; set; }
}
