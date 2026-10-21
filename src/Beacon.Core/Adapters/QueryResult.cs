using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Anomaly;
using Beacon.Core.Models.Recipients;

namespace Beacon.Core.Adapters;

public class QueryResult
{
    public required string QueryResults { get; init; }

    public required int TotalRecords { get; init; }

    public required string DataSourceName { get; init; }

    public required string SqlQuery { get; init; }

    public bool ShowQuery { get; set; } = true;

    public int? MaxRows { get; set; }

    public List<RecipientData> Recipients { get; set; } = [];

    public List<IDictionary<string, object?>> TopRecords { get; set; } = [];

    public List<IDictionary<string, object?>> AllRecords { get; set; } = [];

    public required string SubscriptionName { get; init; }

    public required int? SubscriptionId { get; init; }

    /// <summary>The data sources a subscription run read; recorded on its execution history.</summary>
    public IReadOnlyList<int> DataSourceIds { get; init; } = [];

    public double ExecutionTimeMs { get; set; }

    public bool SaveResults { get; set; }

    /// <summary>
    /// Indicates whether the query execution was cancelled due to a timeout
    /// </summary>
    public bool TimedOut { get; set; }
}

public class RecipientQueryResult
{
    public required string RecipientDestination { get; init; }

    public required NotificationType RecipientNotificationType { get; init; }

    public required QueryResult QueryResult { get; init; }

    public QueryResultFile? QueryResultFile { get; init; }

    public int? NotificationId { get; init; }

    /// <summary>The recipient being notified, for delivery logs.</summary>
    public int? RecipientId { get; init; }

    public AnomalyEvaluationResult? AnomalyEvaluation { get; init; }

    public string? HeadersJson { get; init; }

    public string? BodyTemplate { get; init; }
}

public class QueryResultFile
{
    public required byte[] Data { get; init; }

    public required string Name { get; init; }

    public required string ContentType { get; init; }

    /// <summary>The rows the file holds, or null when not recorded.</summary>
    public int? RowCount { get; init; }

    /// <summary>The rows the run delivered for the file, or null when not recorded.</summary>
    public int? SourceRowCount { get; init; }

    /// <summary>The file holds fewer rows than the run delivered: it was cut to stay within the attachment size limit.</summary>
    public bool Truncated => RowCount < SourceRowCount;

    /// <summary>
    /// No file is sent: even its column headers are larger than the attachment size limit. <see cref="Data"/> is empty.
    /// </summary>
    public bool Omitted { get; init; }
}