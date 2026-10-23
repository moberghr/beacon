using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Settings;

namespace Beacon.Core.Handlers.AdminSettings;

/// <summary>
/// The LLM connection settings only an Admin may change or test with other values: the provider, the endpoint, the
/// region and Bedrock authentication mode (both decide where requests go), and every credential. A SecurityAdmin
/// changes the rest of the admin settings and tests the stored connection; were the endpoint theirs to change, the
/// stored credentials would follow it. A refusal is a 403, logged with ids only.
/// </summary>
internal static class LlmConnectionSettings
{
    public const string AdminOnlyMessage =
        "Only an Admin can change the LLM provider, endpoint, region, authentication mode or credentials.";

    public const string NewDestinationNeedsCredentialMessage =
        "The stored API key is never sent to a different provider or endpoint: enter the API key (or the AWS secret access key for Bedrock) again when changing them (for Bedrock also the region or authentication mode).";

    /// <summary>
    /// Refuses a caller who is not an Admin a request that changes or supplies any connection setting. A provider and a
    /// region are left as stored when they are null (<paramref name="nullKeepsStored"/>, as a test does) or equal to the
    /// stored ones; an endpoint and credentials only when they are null ("leave as is").
    /// </summary>
    public static void DemandAdminForChanges(
        BeaconPermissionSet permissions,
        BeaconActor actor,
        ILogger logger,
        AppSettingsData current,
        LlmConnectionRequest request,
        bool nullKeepsStored)
    {
        if (permissions.IsAdmin || KeepsStoredConnection(current, request, nullKeepsStored))
        {
            return;
        }

        throw AccessRefusal.Of(logger, actor, "LLM connection settings", null, AdminOnlyMessage);
    }

    /// <summary>
    /// Refuses (400) a request that points the connection at another provider, endpoint or (Bedrock) region or authentication mode without supplying the
    /// credential again: a stored credential is never sent to a destination it was not entered for. Nothing is refused
    /// while no credential is stored. A null provider or endpoint keeps the stored one.
    /// </summary>
    public static void DemandCredentialsForNewDestination(AppSettingsData current, LlmConnectionRequest request)
    {
        var providerChanged = request.Provider != null && request.Provider != current.LlmProvider;
        var endpointChanged = !string.IsNullOrWhiteSpace(request.Endpoint)
            && !string.Equals(request.Endpoint.Trim(), current.LlmEndpoint?.Trim(), StringComparison.Ordinal);
        var provider = request.Provider ?? current.LlmProvider;
        var bedrockTargetChanged = provider == AiProvider.Bedrock
            && (request.BedrockAuthMode != current.LlmBedrockAuthMode
                || (request.Region != null && !string.Equals(request.Region.Trim(), current.LlmRegion?.Trim(), StringComparison.Ordinal)));
        if (!providerChanged && !endpointChanged && !bedrockTargetChanged)
        {
            return;
        }

        var missing = provider == AiProvider.Bedrock
            ? !string.IsNullOrEmpty(current.LlmAwsSecretAccessKey) && string.IsNullOrEmpty(request.AwsSecretAccessKey)
            : !string.IsNullOrEmpty(current.LlmApiKey) && string.IsNullOrEmpty(request.ApiKey);
        if (missing)
        {
            throw new InvalidOperationException(NewDestinationNeedsCredentialMessage);
        }
    }

    private static bool KeepsStoredConnection(AppSettingsData current, LlmConnectionRequest request, bool nullKeepsStored)
    {
        var keepsProvider = request.Provider == current.LlmProvider || (nullKeepsStored && request.Provider == null);
        var keepsRegion = string.Equals(request.Region, current.LlmRegion, StringComparison.Ordinal)
            || (nullKeepsStored && request.Region == null);

        return keepsProvider
            && keepsRegion
            && request.BedrockAuthMode == current.LlmBedrockAuthMode
            && request.ApiKey == null
            && request.Endpoint == null
            && request.SessionToken == null
            && request.AwsAccessKeyId == null
            && request.AwsSecretAccessKey == null;
    }
}

/// <summary>The connection settings a request to change or test the LLM connection carries.</summary>
internal sealed record LlmConnectionRequest(
    AiProvider? Provider,
    string? Region,
    BedrockAuthMode BedrockAuthMode,
    string? ApiKey,
    string? Endpoint,
    string? SessionToken,
    string? AwsAccessKeyId,
    string? AwsSecretAccessKey);
