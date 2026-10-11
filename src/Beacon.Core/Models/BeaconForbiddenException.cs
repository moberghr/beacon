namespace Beacon.Core.Models;

/// <summary>
/// The caller may see the resource but is not allowed to take the action (a project role, an owner or assignee rule,
/// an Admin-only change): HTTP 403 without the message. Thrown only for authorization refusals, so a 403 never comes from
/// an unrelated failure such as a file the process may not open. Not sealed: Beacon tells its own refusals apart by
/// subtype (a run refused right before its SQL).
/// </summary>
public class BeaconForbiddenException(string message) : BeaconException(message);
