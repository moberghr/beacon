namespace Beacon.Core;

/// <summary>
/// Password sign-in through Beacon's login form (<c>Beacon:UserManagement:LocalLogin</c>), bound onto
/// <see cref="UserManagementOptions.LocalLogin"/> at startup. On by default, for compatibility. With single sign-on, turn it
/// off so people sign in through the identity provider (and its multi-factor authentication and conditional access).
/// Off, <c>POST /beacon/api/auth/login</c> is not mapped, Beacon's password providers refuse every account, and the login
/// page shows no password form; only the <see cref="BreakGlassUserNames"/> may still sign in with a password, for
/// emergencies (the endpoint then stays mapped for them, and each such sign-in is logged at Warning).
/// </summary>
public class LocalLoginOptions
{
    public const string SectionName = "Beacon:UserManagement:LocalLogin";

    /// <summary>At most this many break-glass accounts: the list is for emergencies, not for everyday sign-in.</summary>
    public const int MaxBreakGlassUserNames = 3;

    /// <summary>Password sign-in through the login form is open to every account. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// User names of the internal accounts that may sign in with a password while <see cref="Enabled"/> is off: emergency
    /// (break-glass) access when the identity provider is unavailable. Typically the super admin. Each is compared exactly
    /// (case included) with the user name the account is stored with, whether its holder signs in with that name or an
    /// e-mail address. Empty by default.
    /// </summary>
    public List<string> BreakGlassUserNames { get; set; } = new();

    /// <summary>The password form is served: local login is on, or a break-glass account is configured.</summary>
    public bool IsOffered => Enabled || BreakGlassUserNames.Any(x => !string.IsNullOrWhiteSpace(x));

    /// <summary>
    /// <paramref name="storedUserName"/>, the user name an account is stored with, is a break-glass account: an exact match
    /// (case included) with an entry of <see cref="BreakGlassUserNames"/> (surrounding space in the entry ignored).
    /// </summary>
    public bool IsBreakGlass(string? storedUserName)
    {
        if (string.IsNullOrWhiteSpace(storedUserName))
        {
            return false;
        }

        return BreakGlassUserNames.Any(x => string.Equals(x?.Trim(), storedUserName, StringComparison.Ordinal));
    }

    /// <summary>The account stored with the user name <paramref name="userName"/> may sign in with a password.</summary>
    public bool Permits(string? userName)
    {
        return Enabled || IsBreakGlass(userName);
    }

    /// <summary>Fails startup for a blank break-glass entry or more than <see cref="MaxBreakGlassUserNames"/> of them.</summary>
    public void Validate()
    {
        if (BreakGlassUserNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException($"{SectionName}:BreakGlassUserNames may not contain a blank entry.");
        }

        if (BreakGlassUserNames.Count > MaxBreakGlassUserNames)
        {
            throw new InvalidOperationException(
                $"{SectionName}:BreakGlassUserNames lists {BreakGlassUserNames.Count} accounts; at most {MaxBreakGlassUserNames} are allowed: break-glass accounts are for emergencies only.");
        }
    }
}
