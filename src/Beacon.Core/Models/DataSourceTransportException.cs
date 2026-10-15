namespace Beacon.Core.Models;

/// <summary>
/// A data-source connection refused because it does not authenticate the server over TLS. The message names the option
/// to change and what to set it to, never a value read from the connection data. HTTP 400 with the message.
/// </summary>
public sealed class DataSourceTransportException(string message) : BeaconException(message);
