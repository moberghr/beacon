namespace Beacon.Core.Models.Providers.CloudWatch;

[Obsolete("The CloudWatch connector reads its own configuration type; this one is unused and removed in 6.0.")]
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
