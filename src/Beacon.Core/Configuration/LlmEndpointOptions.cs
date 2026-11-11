using System.Net;
using Beacon.Core.Http;

namespace Beacon.Core.Configuration;

/// <summary>
/// The endpoint rule for a configured LLM endpoint (Azure OpenAI today), bound from <c>Beacon:LLM</c> next to the
/// provider settings. An endpoint must use https; http is accepted only for a loopback host (a local model gateway) or
/// while <see cref="AllowInsecureEndpoint"/> is set. The LLM clients also never connect to a link-local or cloud metadata
/// address, whatever the endpoint says.
/// </summary>
public sealed class LlmEndpointOptions
{
    public const string SectionName = "Beacon:LLM";

    /// <summary>
    /// Accepts an http endpoint on any host. For development and closed test networks only; the provider's credentials
    /// and every prompt then travel unencrypted. Default false.
    /// </summary>
    public bool AllowInsecureEndpoint { get; set; }
}

/// <summary>The scheme rule for a configured LLM endpoint, shared by the settings save, the connection test and the provider.</summary>
internal static class LlmEndpointRule
{
    public const string Message =
        "The LLM endpoint must use https (http only for a loopback host such as localhost, or while Beacon:LLM:AllowInsecureEndpoint is set).";

    /// <summary>Whether an absolute endpoint URI meets the rule. A URI that is neither http nor https never does.</summary>
    public static bool IsAllowed(Uri endpoint, bool allowInsecureEndpoint)
    {
        if (endpoint.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        if (endpoint.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        return allowInsecureEndpoint || IsLoopbackHost(endpoint);
    }

    /// <summary>
    /// Whether an endpoint as an Admin typed it meets the rule. Text that is not an absolute URI is left to the provider's
    /// own check, which names its problem.
    /// </summary>
    public static bool IsAllowed(string endpoint, bool allowInsecureEndpoint)
    {
        return !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || IsAllowed(uri, allowInsecureEndpoint);
    }

    private static bool IsLoopbackHost(Uri endpoint)
    {
        var host = endpoint.IdnHost.Trim('[', ']');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address)
            && OutboundAddresses.Classify(address) == OutboundAddressKind.Loopback;
    }
}
