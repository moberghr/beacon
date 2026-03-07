namespace Semantico.Core.Models.Providers.CloudWatch;

public class CloudWatchConfiguration
{
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    public string? SessionToken { get; set; }
    public string? AssumeRoleArn { get; set; }
    public required string Region { get; set; }
    public List<string> LogGroups { get; set; } = new();
    public string? Namespace { get; set; }
    public string? ProfileName { get; set; }
    public int QueryTimeoutSeconds { get; set; } = 300;
}
