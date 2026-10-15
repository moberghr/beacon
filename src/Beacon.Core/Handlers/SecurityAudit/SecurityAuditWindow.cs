namespace Beacon.Core.Handlers.SecurityAudit;

/// <summary>The time window a read or a verification of the security audit trail covers.</summary>
internal static class SecurityAuditWindow
{
    /// <summary>
    /// The window as UTC (an offset-less value is taken as UTC, the storage kind; Npgsql refuses unspecified kinds), after
    /// checking both ends are given, in order and at most <paramref name="maxDays"/> days apart.
    /// </summary>
    public static (DateTime From, DateTime To) Validate(DateTime? from, DateTime? to, int maxDays)
    {
        if (from == null || to == null)
        {
            throw new InvalidOperationException("The security audit 'from' and 'to' times are required.");
        }

        var utcFrom = ToUtc(from.Value);
        var utcTo = ToUtc(to.Value);
        if (utcTo < utcFrom)
        {
            throw new InvalidOperationException("The security audit 'to' time must not be earlier than 'from'.");
        }

        if (utcTo - utcFrom > TimeSpan.FromDays(maxDays))
        {
            throw new InvalidOperationException($"The security audit window must not exceed {maxDays} days.");
        }

        return (utcFrom, utcTo);
    }

    private static DateTime ToUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}
