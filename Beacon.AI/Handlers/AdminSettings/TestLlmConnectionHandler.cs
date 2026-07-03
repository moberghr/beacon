using System.Diagnostics;
using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.LlmProviders;

namespace Beacon.AI.Handlers.AdminSettings;

/// <summary>
/// AI-side implementation of <see cref="ILlmConnectionTester"/>. Builds an ephemeral provider
/// from the supplied parameters and runs a single tiny prompt — never mutates the live provider.
/// </summary>
public sealed class LlmConnectionTester(LlmRequestQueue requestQueue) : ILlmConnectionTester
{
    private const string PingPrompt = "Respond with the single word OK.";
    private const int PingMaxTokens = 16;

    public async Task<LlmConnectionTestResult> TestAsync(LlmConnectionTestParameters parameters, CancellationToken cancellationToken)
    {
        var config = new LlmConfiguration
        {
            Provider = parameters.Provider,
            ApiKey = parameters.ApiKey,
            Endpoint = parameters.Endpoint,
            Region = parameters.Region,
            SessionToken = parameters.SessionToken,
            AwsAccessKeyId = parameters.AwsAccessKeyId,
            AwsSecretAccessKey = parameters.AwsSecretAccessKey,
            BedrockAuthMode = parameters.BedrockAuthMode,
            Model = parameters.Model,
        };

        var factory = new LlmProviderFactory(() => config);
        ILlmProvider provider;
        try
        {
            provider = factory.CreateProvider();
        }
        catch (Exception ex)
        {
            return new LlmConnectionTestResult(false, null, config.Model, ex.Message);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Route through LlmRequestQueue (§6.1) so the ephemeral test provider is subject to the
            // same concurrency limit and retry policy as every other LLM call.
            var response = await requestQueue.EnqueueRequestAsync(
                provider,
                new LlmRequest
                {
                    Messages = [new ChatMessage(ConversationRole.User, PingPrompt)],
                    MaxTokens = PingMaxTokens,
                    Temperature = 0m,
                },
                cancellationToken);

            stopwatch.Stop();
            return new LlmConnectionTestResult(
                Ok: true,
                LatencyMs: stopwatch.ElapsedMilliseconds,
                Model: config.Model,
                Error: null,
                Sample: response.Content);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new LlmConnectionTestResult(
                Ok: false,
                LatencyMs: stopwatch.ElapsedMilliseconds,
                Model: config.Model,
                Error: ex.Message);
        }
    }
}
