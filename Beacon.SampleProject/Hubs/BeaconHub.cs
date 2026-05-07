using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Beacon.SampleProject.Hubs;

/// <summary>
/// Real-time push channel for the React shell. Server publishes to authenticated users
/// via <see cref="IHubContext{BeaconHub}"/>; clients receive events scoped to their user id.
/// </summary>
[Authorize(AuthenticationSchemes = Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)]
public sealed class BeaconHub : Hub
{
}

public static class BeaconHubEventNames
{
    public const string JobStatusChanged = "JobStatusChanged";
    public const string NotificationCreated = "NotificationCreated";
    public const string ApprovalUpdated = "ApprovalUpdated";
}

public sealed record JobStatusChangedEvent(string JobId, string State, DateTimeOffset Timestamp);
public sealed record NotificationCreatedEvent(int NotificationId, string Kind);
public sealed record ApprovalUpdatedEvent(int ApprovalId, string Status);
