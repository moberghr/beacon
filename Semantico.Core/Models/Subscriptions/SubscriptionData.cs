using Semantico.Core.Models.Recipients;

namespace Semantico.Core.Models.Subscriptions;

public class SubscriptionData
{
    public int? SubscriptionId { get; set; }

    public int QueryId { get; set; }

    public string QueryName { get; set; }

    public string CronExpression { get; set; }
    
    public int? MaxRows { get; set; }
    
    public bool IncludeAttachment { get; set; } = true;
    
    public bool ShowQuery { get; set; } = true;
    
    /// <summary>
    /// Query execution timeout in seconds. If null, no timeout is applied.
    /// </summary>
    public int? TimeoutSeconds { get; set; }
    
    /// <summary>
    /// Start hour of the execution window (0-23). If null, no time restriction is applied.
    /// </summary>
    public int? ExecutionWindowStartHour { get; set; }
    
    /// <summary>
    /// End hour of the execution window (0-23). If null, no time restriction is applied.
    /// </summary>
    public int? ExecutionWindowEndHour { get; set; }

    public List<RecipientData> Recipients { get; set; } = new();

    public List<SubscriptionParamaterData> Parameters { get; set; } = new();
}