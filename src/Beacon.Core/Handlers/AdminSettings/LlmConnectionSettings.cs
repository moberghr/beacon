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
    string? AwsSecretAccessKey)
{
    // The compiler-generated ToString prints every member; the secrets are printed redacted.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Provider = {Provider}, Region = {Region}, BedrockAuthMode = {BedrockAuthMode}, ApiKey = {RedactedPrint.Of(ApiKey)}, Endpoint = {RedactedPrint.Of(Endpoint)}, SessionToken = {RedactedPrint.Of(SessionToken)}, AwsAccessKeyId = {RedactedPrint.Of(AwsAccessKeyId)}, AwsSecretAccessKey = {RedactedPrint.Of(AwsSecretAccessKey)}");

        return true;
    }
}
