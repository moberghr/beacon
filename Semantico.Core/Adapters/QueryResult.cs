using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Anomaly;
using Semantico.Core.Models.Recipients;

namespace Semantico.Core.Adapters;

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

    public AnomalyEvaluationResult? AnomalyEvaluation { get; init; }

    public string? HeadersJson { get; init; }

    public string? BodyTemplate { get; init; }
}

public class QueryResultFile
{
    public required byte[] Data { get; init; }

    public required string Name { get; init; }

    public required string ContentType { get; init; }
}