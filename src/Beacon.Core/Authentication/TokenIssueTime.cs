using System.Globalization;
using System.Security.Claims;

namespace Beacon.Core.Authentication;

/// <summary>
/// Whether a bearer token predates the instant its user was last signed out everywhere (<c>Users.TokensValidAfter</c>).
/// A token is refused when its <c>iat</c> is earlier than that instant by more than the validation clock skew, and, once
/// the instant is set, when it carries no readable <c>iat</c>. A user never signed out everywhere (no instant) refuses no
/// token by its issue time.
/// </summary>
internal static class TokenIssueTime
{
    private const string IssuedAtClaim = "iat";

    // The NumericDate range DateTimeOffset can represent.
    private const double MinUnixSeconds = -62_135_596_800;
    private const double MaxUnixSeconds = 253_402_300_799;

    public static bool IssuedBeforeSignOut(ClaimsPrincipal token, DateTime? tokensValidAfter, TimeSpan clockSkew)
    {
        if (tokensValidAfter is not { } validAfter)
        {
            return false;
        }

        var issuedAt = ReadIssuedAt(token);
        if (issuedAt == null)
        {
            return true;
        }

        var cutOff = DateTime.SpecifyKind(validAfter, DateTimeKind.Utc) - clockSkew;

        return issuedAt.Value.UtcDateTime < cutOff;
    }

    // The single iat claim as a NumericDate (seconds since the epoch, fractions allowed); null when absent, repeated or
    // unreadable.
    private static DateTimeOffset? ReadIssuedAt(ClaimsPrincipal token)
    {
        var value = BearerUserBinding.ExactClaim(token, IssuedAtClaim);
        if (value == null
            || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds)
            || seconds < MinUnixSeconds
            || seconds > MaxUnixSeconds)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(seconds));
    }
}
