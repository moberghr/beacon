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

    // The compiler-generated ToString prints every member; the secrets are printed redacted.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Provider = {Provider}, ApiKey = {Core.RedactedPrint.Of(ApiKey)}, Endpoint = {Endpoint}, Region = {Region}, SessionToken = {Core.RedactedPrint.Of(SessionToken)}, AwsAccessKeyId = {AwsAccessKeyId}, AwsSecretAccessKey = {Core.RedactedPrint.Of(AwsSecretAccessKey)}, BedrockAuthMode = {BedrockAuthMode}, Model = {Model}, FastModel = {FastModel}, Limits = {Limits}");

        return true;
    }
}

public sealed record ProviderLimits
{
    /// <summary>How many LLM attempts run at once; a change applies to the running queue. Non-positive: the default.</summary>
    public int MaxConcurrentRequests { get; init; } = 50;

    /// <summary>Tokens per minute, enforced only while <see cref="RateLimitsEnabled"/> is on. Non-positive: unlimited.</summary>
    public int TokensPerMinute { get; init; } = 80000;

    /// <summary>Requests per minute, enforced only while <see cref="RateLimitsEnabled"/> is on. Non-positive: unlimited.</summary>
    public int RequestsPerMinute { get; init; } = 1000;

    /// <summary>Recorded only: no usage is stored, so the budget is not enforced.</summary>
    public decimal MonthlyBudget { get; init; } = 100.00m;

    /// <summary>
    /// Whether <see cref="RequestsPerMinute"/> and <see cref="TokensPerMinute"/> are enforced. Off by default: the stored
    /// rates were never applied before, and enforcing them unannounced could throttle prompts that carry a large schema.
    /// </summary>
    public bool RateLimitsEnabled { get; init; }

    /// <summary>How long an interactive request waits for a free slot before it fails as busy. Non-positive: the default.</summary>
    public int InteractiveQueueWaitSeconds { get; init; } = 60;

    /// <summary>How long a background request waits for a free slot before it fails as busy. Non-positive: the default.</summary>
    public int BackgroundQueueWaitSeconds { get; init; } = 300;

    /// <summary>How many requests may wait for a slot at once; one more fails as busy at once. Non-positive: the default.</summary>
    public int MaxQueuedRequests { get; init; } = 200;
}
