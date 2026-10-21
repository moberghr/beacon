namespace Beacon.Core.Services;

/// <summary>
/// Stand-ins registered when <see cref="BeaconConfiguration.UseAI"/> is false.
///
/// Core registers its MediatR handlers by unconditional assembly scanning, but
/// <c>TestLlmConnectionHandler</c> and <c>RunEvalHandler</c> take dependencies whose only real
/// implementations live in Beacon.AI and are wired by <c>AddBeaconAI</c>. Without these a host
/// that leaves AI off cannot pass DI validation at all, which made UseAI = false unusable.
/// Every other AI-only service is injected optionally (see <c>JobService</c>); these two are not,
/// so Core supplies an explicit "unavailable" implementation rather than a silent no-op.
/// </summary>
internal sealed class AiDisabledLlmConnectionTester : ILlmConnectionTester
{
    public Task<LlmConnectionTestResult> TestAsync(
        LlmConnectionTestParameters parameters,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException(AiDisabled.Message);
    }
}

/// <summary>The provider catalog of a host without Beacon.AI: AI is off and no provider is installed.</summary>
internal sealed class AiDisabledLlmProviderCatalog : ILlmProviderCatalog
{
    public bool AiEnabled => false;

    public IReadOnlyList<LlmProviderInfo> Registered => [];
}

/// <inheritdoc cref="AiDisabledLlmConnectionTester"/>
internal sealed class AiDisabledMcpEvalService : IMcpEvalService
{
    public Task<int> StartRunAsync(int? projectId, int? userId, CancellationToken ct)
    {
        throw new InvalidOperationException(AiDisabled.Message);
    }

    public Task RunAsync(int runId, CancellationToken ct)
    {
        throw new InvalidOperationException(AiDisabled.Message);
    }

    public Task<CaseEvaluation> EvaluateCasePassesAsync(
        int dataSourceId,
        int projectId,
        string question,
        string goldSql,
        string? goldResultFingerprint,
        string? extraContext,
        CancellationToken ct)
    {
        throw new InvalidOperationException(AiDisabled.Message);
    }
}

internal static class AiDisabled
{
    public const string Message =
        "This Beacon host runs with UseAI = false, so AI-backed features are unavailable. "
        + "Set UseAI = true and call AddBeaconAI() to enable them.";
}
