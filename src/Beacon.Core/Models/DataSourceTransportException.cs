namespace Beacon.Core.Models;

/// <summary>
/// A data-source connection refused because it does not authenticate the server over TLS, sets a driver option Beacon
/// does not accept, reaches a host <c>Beacon:DataSources:AllowedHosts</c> does not list, or (the HTTP connectors, at
/// connect time) resolves only to addresses the data-source address rules refuse. The message names the option to change
/// and what to set it to, never a value read from the connection data or a resolved address. HTTP 400 with the message.
/// </summary>
public sealed class DataSourceTransportException(string message) : BeaconException(message);
