using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Authentication;

/// <summary>
/// A delay of a break-glass account's password sign-in (<c>Beacon:RateLimits:BreakGlassThrottle</c>) that a wrong password
/// just set. A refused sign-in hands it to whoever answers the attempt, which records it as the attempt's one security audit
/// event, so setting a delay costs what every refused attempt costs.
/// </summary>
internal sealed record BreakGlassDelay(int UserId, int FailedAttempts, DateTime Until, int DelaySeconds)
{
    /// <summary>The <c>auth.user_locked_out</c> event of the delay, by <paramref name="actor"/>.</summary>
    public SecurityAuditEntry AuditEntry(SecurityAuditActor actor)
    {
        return new SecurityAuditEntry(SecurityAuditActions.UserLockedOut)
            .On(SecurityAuditResources.User, UserId)
            .By(actor)
            .With(new SecurityAuditDetails()
                .Add("failedAttempts", FailedAttempts)
                .Add("lockedUntil", Until)
                .Add("breakGlassDelay", true)
                .Add("delaySeconds", DelaySeconds));
    }
}
