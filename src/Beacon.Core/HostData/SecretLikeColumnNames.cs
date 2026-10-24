using System.Text.RegularExpressions;

namespace Beacon.Core.HostData;

/// <summary>
/// Built-in hard-exclude for host-exposed columns whose name looks like a secret. A matching column is excluded
/// (invisible in metadata, rejected at execution) no matter what the host's predicates say; the only way back is
/// the explicit <see cref="HostDbContextOptions.IncludeSecretLikeColumn"/> override.
/// <list type="bullet">
/// <item>Substring match (case-insensitive): password, passwd, passw, pswd, passphrase, passcode, secret, token, apikey,
/// api_key, privatekey, private_key, credential, connectionstring, connection_string, connstr (so connstring too),
/// securitystamp, security_stamp, recoverycode, recovery_code, and one-word key names (accesskey, secretkey, signingkey,
/// authkey, encryptionkey, masterkey, licensekey, sharedkey, hmackey, aeskey, rsakey, sshkey); a name starting or ending in
/// pwd (<c>userpwd</c>).</item>
/// <item>Whole-word match on the camelCase / snake_case words of the name: pin, pwd, pass, salt, hash, otp, totp, hotp, mfa,
/// refresh, pfx, nonce, seed, cvv, cvc — so <c>PasswordHash</c>, <c>pin_code</c>, <c>OtpSeed</c>, <c>RefreshToken</c>,
/// <c>TotpKey</c> match, but <c>Shipping</c> does not.</item>
/// <item>A <c>key</c> word beside a word naming key material (api, access, secret, private, signing, encryption,
/// authenticator, certificate, cert, master, license, hmac, aes, rsa, pgp, gpg, ssh): <c>EncryptionKey</c>,
/// <c>SigningKey</c>, <c>AuthenticatorKey</c>, but not <c>ProductKey</c>. Business words that only sometimes name a
/// secret (a signing date, a certificate number, a recovered amount, a time stamp) do not match on their own.</item>
/// <item>Every column of a table that holds credentials or key material (<see cref="IsSecretLikeTable"/>):
/// <c>AspNetUserTokens</c>, <c>DataProtectionKeys</c>, any table whose name ends in the word Keys, Tokens, Secrets,
/// Credentials, Passwords, Certificates or Otps (or the singular, <c>ApiKey</c> with a key qualifier), or in Codes / Grants beside
/// recovery, device, verification, reset, backup, authorization, auth or persisted (<c>RecoveryCodes</c>,
/// <c>PersistedGrants</c>); and every column of the well-known ASP.NET Core Identity user-token, Data Protection key,
/// Duende / IdentityServer grant and key, and OpenIddict token and authorization entities, whatever their table is called.</item>
/// </list>
/// The match is a best-effort name heuristic: the read-only login should also be denied the secret columns.
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
        "private_key",
        "passphrase",
        "passcode",
        "credential",
        "connectionstring",
        "connection_string",
        "securitystamp",
        "security_stamp",
        "recoverycode",
        "recovery_code",
        "passw",
        "pswd",
        "accesskey",
        "access_key",
        "secretkey",
        "signingkey",
        "signing_key",
        "authkey",
        "auth_key",
        "encryptionkey",
        "masterkey",
        "licensekey",
        "sharedkey",
        "hmackey",
        "aeskey",
        "rsakey",
        "sshkey",
        "connstr"
    ];

    private static readonly HashSet<string> WordMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "pin",
        "salt",
        "hash",
        "otp",
        "totp",
        "hotp",
        "mfa",
        "pwd",
        "pass",
        "refresh",
        "pfx",
        "nonce",
        "seed",
        "cvv",
        "cvc"
    };

    // A table whose name ends in one of these words holds credentials or key material in every column.
    private static readonly HashSet<string> SecretTableWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "keys",
        "tokens",
        "secrets",
        "credentials",
        "passwords",
        "certificates",
        "otps",
        "token",
        "secret",
        "credential",
        "password",
        "certificate",
        "otp"
    };

    // A table ending in codes or grants holds credentials only beside one of these words (RecoveryCodes, DeviceFlowCodes,
    // PersistedGrants); CountryCodes and PostalCodes stay visible.
    private static readonly HashSet<string> SecretCodeQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "recovery",
        "device",
        "verification",
        "reset",
        "backup",
        "authorization",
        "auth",
        "persisted"
    };

    // Entity types (by generic definition or full name) whose every column is a credential or key material.
    private static readonly HashSet<string> SecretEntityTypeNames = new(StringComparer.Ordinal)
    {
        "Microsoft.AspNetCore.Identity.IdentityUserToken`1",
        "Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey",
        "Duende.IdentityServer.EntityFramework.Entities.PersistedGrant",
        "Duende.IdentityServer.EntityFramework.Entities.DeviceFlowCodes",
        "Duende.IdentityServer.EntityFramework.Entities.Key",
        "IdentityServer4.EntityFramework.Entities.PersistedGrant",
        "IdentityServer4.EntityFramework.Entities.DeviceFlowCodes",
        "OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken`1",
        "OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization`1"
    };

    // Value-side additions: names that hold credentials or key material, matched anywhere in the name.
    private static readonly string[] ValueSubstringMarkers =
    [
        "credential",
        "cookie",
        "passcode",
        "passphrase",
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
        "authenticator",
        "certificate",
        "cert",
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

        List<string> segments;
        try
        {
            segments = WordSplitter.Matches(name)
                .Select(x => x.Value)
                .ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            // A name that cannot be split in time is treated as secret-like.
            return true;
        }

        return segments.Any(x => WordMarkers.Contains(x))
            || IsKeyMaterial(segments)
            || name.EndsWith("pwd", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("pwd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when every column of the table <paramref name="tableName"/> is treated as a secret: <c>AspNetUserTokens</c>,
    /// <c>DataProtectionKeys</c>, and any table whose name ends in the word Keys or Tokens (<c>ApiKeys</c>,
    /// <c>signing_keys</c>, <c>RefreshTokens</c>).
    /// </summary>
    public static bool IsSecretLikeTable(string? tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return false;
        }

        List<string> segments;
        try
        {
            segments = WordSplitter.Matches(tableName)
                .Select(x => x.Value)
                .ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            // A table name that cannot be split in time is treated as holding secrets.
            return true;
        }

        if (segments.Count == 0)
        {
            return false;
        }

        var last = segments[^1];
        var preceding = segments.Take(segments.Count - 1).ToList();

        return SecretTableWords.Contains(last)
            || (last.Equals("key", StringComparison.OrdinalIgnoreCase) && (preceding.Count == 0 || preceding.Any(x => KeyQualifiers.Contains(x))))
            || ((last.Equals("codes", StringComparison.OrdinalIgnoreCase) || last.Equals("code", StringComparison.OrdinalIgnoreCase)
                || last.Equals("grants", StringComparison.OrdinalIgnoreCase) || last.Equals("grant", StringComparison.OrdinalIgnoreCase))
                && preceding.Any(x => SecretCodeQualifiers.Contains(x)));
    }

    /// <summary>
    /// True when <paramref name="column"/> is hard-excluded from a host data source: its column or property name is
    /// secret-like (<see cref="IsSecretLike(string?)"/>), its table holds secrets (<see cref="IsSecretLikeTable"/>), or its
    /// entity is (or derives from) the ASP.NET Core Identity user-token or Data Protection key entity.
    /// </summary>
    public static bool IsSecretLikeColumn(HostColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        return IsSecretLike(column.Name)
            || IsSecretLike(column.PropertyName)
            || IsSecretLikeTable(column.Table)
            || IsSecretEntity(column.EntityType);
    }

    /// <summary>
    /// True when the values of <paramref name="name"/> must not be read to describe the column: everything
    /// <see cref="IsSecretLike(string?)"/> matches, plus the names of sessions, cookies and signatures
    /// (<c>session_id</c>, <c>request_signature</c>, <c>dsn</c>, <c>hmac</c>). Words such as session, signature, dsn or hmac
    /// match as name segments (camelCase or snake_case), never inside a longer word; a <c>key</c> segment only beside api,
    /// access, secret, private, signing, encryption, authenticator, certificate, cert, master, license, hmac, aes, rsa, pgp,
    /// gpg or ssh, or as the whole name (<c>key</c>,
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

        return segments.Any(x => ValueWordMarkers.Contains(x)) || IsKeyMaterial(segments);
    }

    // A "key" word beside a word naming key material.
    private static bool IsKeyMaterial(List<string> segments)
    {
        return segments.Any(x => x.Equals("key", StringComparison.OrdinalIgnoreCase))
            && segments.Any(x => KeyQualifiers.Contains(x));
    }

    private static bool IsSecretEntity(Type? entityType)
    {
        for (var type = entityType; type != null; type = type.BaseType)
        {
            var name = type.IsGenericType ? type.GetGenericTypeDefinition().FullName : type.FullName;
            if (name != null && SecretEntityTypeNames.Contains(name))
            {
                return true;
            }
        }

        return false;
    }
}
