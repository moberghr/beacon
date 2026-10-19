namespace Beacon.Core.Models.QueryExecutionHistory;

public class ExecutionTimeDataPoint
{
    public DateTime Date { get; set; }

    public double AvgExecutionTimeMs { get; set; }

    public double MinExecutionTimeMs { get; set; }

    public double MaxExecutionTimeMs { get; set; }
}
