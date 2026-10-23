using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.AdminSettings;

internal sealed class TestLlmConnectionHandler(
    IAppSettingsService settingsService,
    ILlmConnectionTester tester,
    IBeaconActorAccessor actorAccessor,
    IBeaconPermissionAccessor permissionAccessor,
    ILlmProviderCatalog providerCatalog,
    ILogger<TestLlmConnectionHandler> logger)
    : IRequestHandler<TestLlmConnectionCommand, TestLlmConnectionResult>
{
    public async Task<TestLlmConnectionResult> Handle(TestLlmConnectionCommand request, CancellationToken cancellationToken)
    {
        // Merge incoming form values with persisted secrets — UI sends `null` for "leave as is".
        var current = await settingsService.GetSettingsAsync(cancellationToken);

        // A SecurityAdmin tests the stored connection only: the stored credentials never go to an endpoint they chose.
        var connectionRequest = new LlmConnectionRequest(
            request.LlmProvider,
            request.LlmRegion,
            request.LlmBedrockAuthMode,
            request.LlmApiKey,
            request.LlmEndpoint,
            request.LlmSessionToken,
            request.LlmAwsAccessKeyId,
            request.LlmAwsSecretAccessKey);
        LlmConnectionSettings.DemandAdminForChanges(
            await permissionAccessor.GetCurrentAsync(cancellationToken),
            await actorAccessor.GetCurrentAsync(cancellationToken),
            logger,
            current,
            connectionRequest,
            nullKeepsStored: true);
        LlmConnectionSettings.DemandCredentialsForNewDestination(current, connectionRequest);

        LlmProviderInstallation.DemandInstalled(providerCatalog, request.LlmProvider, current.LlmProvider);

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

/// <summary>
/// Tests an LLM connection. For an Admin or a SecurityAdmin; only an Admin tests with another provider, endpoint, region,
/// Bedrock authentication mode or credentials than the stored ones (403 otherwise, <see cref="LlmConnectionSettings"/>).
/// </summary>
[ProjectScopeExempt("gated by the SecurityAdmin permission: settings, no project data; another LLM connection stays an Admin's to test", Policy = BeaconPermissions.SecurityAdminPolicy)]
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
