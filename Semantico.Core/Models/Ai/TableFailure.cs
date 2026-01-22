namespace Semantico.Core.Models.Ai;

public class TableFailure
{
    public string TableName { get; set; } = null!;
    public string ErrorMessage { get; set; } = null!;
    public int RetryCount { get; set; }
    public DateTime LastAttempt { get; set; }
}
