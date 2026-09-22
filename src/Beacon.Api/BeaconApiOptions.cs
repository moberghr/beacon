using Microsoft.AspNetCore.SignalR;

namespace Beacon.Api;

/// <summary>
/// Configures what <c>AddBeaconApiServices</c> / <c>MapBeaconApi</c> wire up.
/// Realtime (the SignalR hub at <c>/beacon/api/hub</c> and the push-backed
/// <c>IApprovalNotifier</c>) is ON by default, so a host needs no SignalR wiring of its own.
/// Turning it off means the hub route is not mapped, no SignalR services are registered, and
/// approval notifications are dropped by the explicitly-named <c>RealtimeDisabledApprovalNotifier</c>
/// instead of being pushed to clients.
/// </summary>
public sealed class BeaconApiOptions
{
    public bool Realtime { get; set; } = true;

    /// <summary>
    /// Applied to the <see cref="ISignalRServerBuilder"/> returned by <c>AddSignalR()</c> when
    /// <see cref="Realtime"/> is enabled — the hook for protocol and backplane configuration.
    /// </summary>
    public Action<ISignalRServerBuilder>? ConfigureSignalR { get; set; }
}
