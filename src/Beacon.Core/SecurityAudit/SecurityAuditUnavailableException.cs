using Beacon.Core.Models;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// An action refused because its security audit event could not be written while <c>Beacon:Audit:Required</c> is on
/// (<see cref="ISecurityAuditRecorder.RecordRequiredAsync"/>). The message is generic: the caller answers with the action's
/// own generic failure, and the cause is in the log stream (9107, 9115).
/// </summary>
public sealed class SecurityAuditUnavailableException()
    : BeaconException("The action was refused: its security audit event could not be recorded.");
