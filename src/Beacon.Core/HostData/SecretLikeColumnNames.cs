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
/// <para>
/// <see cref="IsSecretLikeForValues"/> is the wider net for the values Beacon reads to describe a column (sample values,
/// value grounding, documentation enum discovery), where a false positive only costs the column its examples.
/// </para>
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

    // Value-side additions: names that hold credentials or key material, matched anywhere in the name.
    private static readonly string[] ValueSubstringMarkers =
    [
        "credential",
        "cookie",
        "passcode",
        "passphrase",
        "accesskey",
        "access_key",
        "secretkey",
        "signingkey",
        "signing_key",
        "authkey",
        "auth_key",
        "sessionid",
        "sessionkey",
        "connectionstring",
        "connection_string",
        "webhookurl",
        "webhook_url"
    ];

    // Value-side additions matched as whole name segments, so turnkey_id, monkey, pinned, hashtag or signatory stay sampled.
    private static readonly HashSet<string> ValueWordMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "session",
        "pwd",
        "totp",
        "hotp",
        "signature",
        "dsn",
        "bearer",
        "jwt",
        "hmac"
    };

    // A "key" segment names key material only beside one of these: a surrogate key (ProductKey, CurrencyKey, ClientKey)
    // does not.
    private static readonly HashSet<string> KeyQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "api",
        "access",
        "secret",
        "private",
        "signing",
        "encryption",
        "master",
        "license",
        "hmac",
        "aes",
        "rsa",
        "pgp",
        "gpg",
        "ssh"
    };

    private static readonly Regex WordSplitter = new(
        @"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

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

        try
        {
            return WordSplitter.Matches(name)
                .Select(x => x.Value)
                .Any(x => WordMarkers.Contains(x));
        }
        catch (RegexMatchTimeoutException)
        {
            // A name that cannot be split in time is treated as secret-like.
            return true;
        }
    }

    /// <summary>
    /// True when the values of <paramref name="name"/> must not be read to describe the column: everything
    /// <see cref="IsSecretLike"/> matches, plus the names of credentials, sessions, cookies, signatures, connection strings
    /// and key material (<c>client_credential</c>, <c>session_id</c>, <c>request_signature</c>, <c>dsn</c>,
    /// <c>encryption_key</c>, <c>hmac_key</c>). Words such as session, pwd, signature, dsn or hmac match as name segments
    /// (camelCase or snake_case), never inside a longer word; a <c>key</c> segment only beside api, access, secret, private,
    /// signing, encryption, master, license, hmac, aes, rsa, pgp, gpg or ssh, or as the whole name (<c>key</c>,
    /// <c>apikey</c>) — a surrogate key such as <c>ProductKey</c> or <c>ClientKey</c> keeps its samples, and so does a name
    /// that is only <c>private</c>. Anything holding <c>secret</c> (<c>client_secret</c>, <c>hmac_secret</c>) is excluded.
    /// </summary>
    public static bool IsSecretLikeForValues(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (IsSecretLike(name))
        {
            return true;
        }

        foreach (var marker in ValueSubstringMarkers)
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (name.Equals("key", StringComparison.OrdinalIgnoreCase) || name.Equals("apikey", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        List<string> segments;
        try
        {
            segments = WordSplitter.Matches(name)
                .Select(x => x.Value)
                .ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }

        return segments.Any(x => ValueWordMarkers.Contains(x))
            || (segments.Any(x => x.Equals("key", StringComparison.OrdinalIgnoreCase)) && segments.Any(x => KeyQualifiers.Contains(x)));
    }
}
