using Beacon.Core.Data.Enums;

namespace Beacon.AI.Models.Configuration;

/// <summary>
/// Immutable snapshot of LLM provider configuration. Use <c>with</c> expressions to derive
/// updated copies; never mutate fields after construction. <see cref="LlmProviderManager"/>
/// atomically swaps the active instance.
/// </summary>
public sealed record LlmConfiguration
{
    public AiProvider Provider { get; init; }
    public string ApiKey { get; init; } = string.Empty;
    public string? Endpoint { get; init; }
    public string? Region { get; init; }
    public string? SessionToken { get; init; }
    public string? AwsAccessKeyId { get; init; }
    public string? AwsSecretAccessKey { get; init; }
    public BedrockAuthMode BedrockAuthMode { get; init; } = BedrockAuthMode.IamRole;
    public string Model { get; init; } = string.Empty;
    public string? FastModel { get; init; }
    public ProviderLimits Limits { get; init; } = new();
}

public sealed record ProviderLimits
{
    public int MaxConcurrentRequests { get; init; } = 50;
    public int TokensPerMinute { get; init; } = 80000;
    public int RequestsPerMinute { get; init; } = 1000;
    public decimal MonthlyBudget { get; init; } = 100.00m;

    /// <summary>How long an interactive request waits for a free slot before it fails as busy. Non-positive: the default.</summary>
    public int InteractiveQueueWaitSeconds { get; init; } = 60;

    /// <summary>How long a background request waits for a free slot before it fails as busy. Non-positive: the default.</summary>
    public int BackgroundQueueWaitSeconds { get; init; } = 300;

    /// <summary>How many requests may wait for a slot at once; one more fails as busy at once. Non-positive: the default.</summary>
    public int MaxQueuedRequests { get; init; } = 200;
}
