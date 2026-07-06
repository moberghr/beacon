namespace Beacon.Core.Data.Enums;

public enum HealthStatus
{
    Green = 1,    // >=90% success rate
    Amber = 2,    // 70-90% success rate
    Red = 3,      // <70% success rate
    Stalled = 4   // 0 executions in the selected window for a subscription older than the grace period
}
