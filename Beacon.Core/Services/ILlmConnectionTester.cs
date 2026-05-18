using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services;

/// <summary>
/// Tests an LLM provider connection against an ephemeral configuration without mutating the live provider.
/// Implemented in Beacon.AI so Core stays free of provider-specific dependencies.
/// </summary>
public interface ILlmConnectionTester
{
    Task<LlmConnectionTestResult> TestAsync(LlmConnectionTestParameters parameters, CancellationToken cancellationToken);
}

public record LlmConnectionTestParameters(
    AiProvider Provider,
    string Model,
    string? Region,
    BedrockAuthMode BedrockAuthMode,
    string ApiKey,
    string? Endpoint,
    string? SessionToken,
    string? AwsAccessKeyId,
    string? AwsSecretAccessKey);

public record LlmConnectionTestResult(
    bool Ok,
    long? LatencyMs,
    string? Model,
    string? Error,
    string? Sample = null);
