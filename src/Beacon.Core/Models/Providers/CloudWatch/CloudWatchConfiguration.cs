namespace Beacon.Core.Models.Providers.CloudWatch;

public class CloudWatchConfiguration
{
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    public string? SessionToken { get; set; }
    public required string Region { get; set; }
    public List<string> LogGroups { get; set; } = new();
    public string? ProfileName { get; set; }
    public int QueryTimeoutSeconds { get; set; } = 300;
}
