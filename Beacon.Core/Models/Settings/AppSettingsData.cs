using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.Settings;

public class AppSettingsData
{
    // General
    public string? BaseUrl { get; set; }

    // LLM
    public AiProvider? LlmProvider { get; set; }
    public string? LlmApiKey { get; set; }
    public string? LlmEndpoint { get; set; }
    public string? LlmRegion { get; set; }
    public string? LlmSessionToken { get; set; }
    public string? LlmAwsAccessKeyId { get; set; }
    public string? LlmAwsSecretAccessKey { get; set; }
    public BedrockAuthMode LlmBedrockAuthMode { get; set; } = BedrockAuthMode.IamRole;
    public string? LlmModel { get; set; }
    public string? LlmFastModel { get; set; }
    public int LlmMaxConcurrentRequests { get; set; } = 50;
    public int LlmTokensPerMinute { get; set; } = 80000;
    public int LlmRequestsPerMinute { get; set; } = 1000;
    public decimal LlmMonthlyBudget { get; set; } = 100.00m;
}
