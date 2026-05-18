using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.AdminSettings;

internal sealed class TestLlmConnectionHandler(
    IAppSettingsService settingsService,
    ILlmConnectionTester tester)
    : IRequestHandler<TestLlmConnectionCommand, TestLlmConnectionResult>
{
    public async Task<TestLlmConnectionResult> Handle(TestLlmConnectionCommand request, CancellationToken cancellationToken)
    {
        // Merge incoming form values with persisted secrets — UI sends `null` for "leave as is".
        var current = await settingsService.GetSettingsAsync(cancellationToken);

        var provider = request.LlmProvider ?? current.LlmProvider ?? AiProvider.OpenAI;
        var model = request.LlmModel ?? current.LlmModel;

        if (string.IsNullOrWhiteSpace(model))
        {
            return new TestLlmConnectionResult(false, null, null, "Model is required to test the connection.");
        }

        var parameters = new LlmConnectionTestParameters(
            Provider: provider,
            Model: model,
            Region: request.LlmRegion ?? current.LlmRegion,
            BedrockAuthMode: request.LlmBedrockAuthMode,
            ApiKey: request.LlmApiKey ?? current.LlmApiKey ?? string.Empty,
            Endpoint: request.LlmEndpoint ?? current.LlmEndpoint,
            SessionToken: request.LlmSessionToken ?? current.LlmSessionToken,
            AwsAccessKeyId: request.LlmAwsAccessKeyId ?? current.LlmAwsAccessKeyId,
            AwsSecretAccessKey: request.LlmAwsSecretAccessKey ?? current.LlmAwsSecretAccessKey);

        var result = await tester.TestAsync(parameters, cancellationToken);

        return new TestLlmConnectionResult(result.Ok, result.LatencyMs, result.Model, result.Error, result.Sample);
    }
}

public record TestLlmConnectionCommand(
    AiProvider? LlmProvider,
    string? LlmModel,
    string? LlmRegion,
    BedrockAuthMode LlmBedrockAuthMode,
    string? LlmApiKey,
    string? LlmEndpoint,
    string? LlmSessionToken,
    string? LlmAwsAccessKeyId,
    string? LlmAwsSecretAccessKey) : IRequest<TestLlmConnectionResult>;

public record TestLlmConnectionResult(
    bool Ok,
    long? LatencyMs,
    string? Model,
    string? Error,
    string? Sample = null);
