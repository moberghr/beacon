namespace Semantico.Core.Data.Enums;

public enum NotificationTrigger
{
    /// <summary>
    /// Send notification if result count is different (increased or decreased) from last execution
    /// </summary>
    OnResultCountChange = 1,

    /// <summary>
    /// Always send notification on every execution
    /// </summary>
    Always = 2,

    /// <summary>
    /// Send notification only if result count is larger than last execution
    /// </summary>
    OnResultCountIncrease = 3
}
