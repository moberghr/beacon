using Beacon.Core.Data.Entities;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// How a stored security audit event is judged, shared by the Auditor's verification, the scheduled verification and the
/// retention purge: its time must lie in the range a genuine event can have (no later than now plus a clock skew, no
/// earlier than the retention, or the longest retention accepted, plus the time a daily purge may lag), and its HMAC must
/// match under a key this deployment holds.
/// </summary>
internal static class SecurityAuditVerification
{
    /// <summary>How far ahead of this server's clock an event may be dated (another node's clock).</summary>
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>How far past the retention an event may still be stored: a daily purge runs at most a day late, plus a margin.</summary>
    internal static readonly TimeSpan PurgeLag = TimeSpan.FromDays(2);

    /// <summary>The earliest and latest time a genuine event stored now can carry.</summary>
    public static (DateTime Earliest, DateTime Latest) PlausibleRange(DateTimeOffset now, int? retentionDays)
    {
        var utcNow = now.UtcDateTime;
        var keptDays = retentionDays ?? Configuration.SecurityAuditOptions.MaxRetentionDays;

        return (utcNow.AddDays(-keptDays) - PurgeLag, utcNow + ClockSkew);
    }

    /// <summary>The verdict on <paramref name="auditEvent"/>: out of range, or how its HMAC verifies.</summary>
    public static string VerdictOf(SecurityAuditEvent auditEvent, (DateTime Earliest, DateTime Latest) range, SecurityAuditIntegrity integrity)
    {
        if (auditEvent.CreatedTime < range.Earliest || auditEvent.CreatedTime > range.Latest)
        {
            return SecurityAuditVerdicts.OutOfRange;
        }

        return integrity.Verify(auditEvent) switch
        {
            SecurityAuditVerdict.Intact => SecurityAuditVerdicts.Intact,
            SecurityAuditVerdict.UnknownKey => SecurityAuditVerdicts.UnknownKey,
            _ => SecurityAuditVerdicts.Modified
        };
    }
}

/// <summary>What verification says about one stored security audit event.</summary>
public static class SecurityAuditVerdicts
{
    /// <summary>The event's fields match its HMAC.</summary>
    public const string Intact = "intact";

    /// <summary>The fields do not match the HMAC: the row was changed, or written without the key.</summary>
    public const string Modified = "modified";

    /// <summary>The row names a key this deployment does not hold (a key no longer configured, or a forged id).</summary>
    public const string UnknownKey = "unknown_key";

    /// <summary>
    /// No event with this id in the window and range verification covers: never stored, deleted, or dated outside the
    /// range a genuine event can have. Never reported as intact.
    /// </summary>
    public const string NotFound = "not_found";

    /// <summary>Dated outside the range a genuine event can have (in the future, or older than the retention allows).</summary>
    internal const string OutOfRange = "out_of_range";
}
