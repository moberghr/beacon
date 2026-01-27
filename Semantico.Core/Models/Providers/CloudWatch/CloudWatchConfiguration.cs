namespace Semantico.Core.Models.Providers.CloudWatch;

public class CloudWatchConfiguration
{
    /// <summary>
    /// AWS Access Key ID (optional if using profile or IAM role)
    /// </summary>
    public string? AccessKeyId { get; set; }

    /// <summary>
    /// AWS Secret Access Key (optional if using profile or IAM role)
    /// </summary>
    public string? SecretAccessKey { get; set; }

    /// <summary>
    /// Session token for temporary credentials (optional)
    /// </summary>
    public string? SessionToken { get; set; }

    /// <summary>
    /// ARN of IAM role to assume for cross-account access (optional)
    /// </summary>
    public string? AssumeRoleArn { get; set; }

    /// <summary>
    /// AWS region (required)
    /// </summary>
    public required string Region { get; set; }

    /// <summary>
    /// List of log group names to query (for Logs Insights queries)
    /// Example: ["/aws/lambda/my-function", "/aws/ecs/my-service"]
    /// </summary>
    public List<string> LogGroups { get; set; } = new();

    /// <summary>
    /// CloudWatch namespace for metrics queries (optional)
    /// Example: "AWS/Lambda", "AWS/RDS", "CustomApp"
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// AWS profile name from ~/.aws/credentials (optional)
    /// </summary>
    public string? ProfileName { get; set; }

    /// <summary>
    /// Query timeout in seconds (default: 300 = 5 minutes)
    /// </summary>
    public int QueryTimeoutSeconds { get; set; } = 300;
}
