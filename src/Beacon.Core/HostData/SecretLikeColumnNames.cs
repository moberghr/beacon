using System.Text.RegularExpressions;

namespace Beacon.Core.HostData;

/// <summary>
/// Built-in hard-exclude for host-exposed columns whose name looks like a secret. A matching column is excluded
/// (invisible in metadata, rejected at execution) no matter what the host's predicates say; the only way back is
/// the explicit <see cref="HostDbContextOptions.IncludeSecretLikeColumn"/> override.
/// <list type="bullet">
/// <item>Substring match (case-insensitive): password, passwd, secret, token, apikey, api_key, privatekey, private_key.</item>
/// <item>Whole-word match on the camelCase / snake_case words of the name: pin, salt, hash, otp, refresh — so
/// <c>PasswordHash</c>, <c>pin_code</c>, <c>OtpSeed</c>, <c>RefreshToken</c> match, but <c>Shipping</c> does not.</item>
/// </list>
/// </summary>
public static class SecretLikeColumnNames
{
    private static readonly string[] SubstringMarkers =
    [
        "password",
        "passwd",
        "secret",
        "token",
        "apikey",
        "api_key",
        "privatekey",
        "private_key"
    ];

    private static readonly HashSet<string> WordMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "pin",
        "salt",
        "hash",
        "otp",
        "refresh"
    };

    private static readonly Regex WordSplitter = new(
        @"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+",
        RegexOptions.Compiled);

    /// <summary>True when <paramref name="name"/> (a column or property name) looks like it holds a secret.</summary>
    public static bool IsSecretLike(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var marker in SubstringMarkers)
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return WordSplitter.Matches(name)
            .Select(x => x.Value)
            .Any(x => WordMarkers.Contains(x));
    }
}
