namespace Beacon.Core.Models;

/// <summary>
/// A request's input is outside what Beacon accepts (too long, too many): answers HTTP 400 with the message, which is
/// written for the caller and never quotes the input.
/// </summary>
public sealed class BeaconValidationException(string message) : BeaconException(message);
