using Semantico.Core.Data.Enums;

namespace Semantico.AI.Models.Configuration;

public class LlmConfiguration
{
    public AiProvider Provider { get; set; }
    public string ApiKey { get; set; } = null!;
    public string? Endpoint { get; set; }
    public string? Region { get; set; } // AWS Region for Bedrock (e.g., "us-east-1")
    public string? SessionToken { get; set; } // AWS Session Token for temporary credentials
    public string Model { get; set; } = null!;
    public string? FastModel { get; set; }
    public ProviderLimits Limits { get; set; } = new();
}

public class ProviderLimits
{
    public int MaxConcurrentRequests { get; set; } = 50;
    public int TokensPerMinute { get; set; } = 80000;
    public int RequestsPerMinute { get; set; } = 1000;
    public decimal MonthlyBudget { get; set; } = 100.00m;
}
