using Beacon.Core.Models;

namespace Beacon.Core.Services.Embed;

/// <summary>
/// An embed request that cannot be served. Its message is the one generic text every refusal shares; <see cref="Reason"/>
/// (an <see cref="EmbedRefusalCodes"/> code or a render outcome) and <see cref="GrantId"/> are for the log and the audit
/// trail only and are never returned to the caller.
/// </summary>
internal sealed class EmbedUnavailableException(string reason, int? grantId = null) : BeaconException(GenericMessage)
{
    public const string GenericMessage = "This view is unavailable.";

    public string Reason { get; } = reason;

    public int? GrantId { get; } = grantId;
}

/// <summary>An embed exchange whose request is invalid (a parameter or lifetime), told to the host's backend that sent it.</summary>
internal sealed class EmbedRequestInvalidException(string message) : BeaconException(message);
