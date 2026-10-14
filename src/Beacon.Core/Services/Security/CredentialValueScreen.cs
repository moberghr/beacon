using System.Text.RegularExpressions;

namespace Beacon.Core.Services.Security;

/// <summary>
/// Value-shaped credential screen: values that look like keys, tokens or password hashes whatever the column is called —
/// a PEM block, a JSON Web Token, a key with a well-known prefix (<c>sk_live_</c>, <c>sk-</c>, <c>AKIA</c>, <c>ghp_</c>,
/// <c>xoxb-</c>, <c>ATATT</c>, <c>AIza</c>, <c>glpat-</c>), a bcrypt, argon2 or crypt hash, a run of more than 32 hex digits
/// (a dash-less 32-digit GUID or MD5 alone is an id, not a credential — a secret-like column name excludes such a column
/// anyway), a base64-like run of 32 or more characters that mixes upper case, lower case and digits unless it is a run of
/// short words joined by <c>-</c>, <c>_</c> or <c>/</c> (a slug, SKU or path), a credential written into free text
/// (<c>password=…</c>, <c>secret: …</c>, <c>api_key=…</c>) or a URL that carries a user name and password
/// (<c>scheme://user:pass@host</c>). A column with one such value keeps none of its samples.
/// </summary>
public static partial class CredentialValueScreen
{
    private const int MinRunLength = 32;

    // A slug, SKU or path is a run of words this long or shorter, joined by a separator.
    private const int MaxSlugWordLength = 12;

    private static readonly char[] SlugSeparators = ['-', '_', '/'];

    [GeneratedRegex(
        @"-----BEGIN |\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{4,}|\b(?:sk|pk|rk)_(?:live|test)_[A-Za-z0-9]{8,}|\bsk[-_][A-Za-z0-9_-]{16,}|\b(?:AKIA|ASIA)[0-9A-Z]{16}\b|\bgh[pousr]_[A-Za-z0-9]{20,}|\bgithub_pat_[A-Za-z0-9_]{20,}|\bxox[abposr]-[A-Za-z0-9-]{8,}|\bATATT[A-Za-z0-9_=-]{10,}|\bAIza[0-9A-Za-z_-]{20,}|\bglpat-[A-Za-z0-9_-]{16,}|\$2[abxy]?\$\d{2}\$[./A-Za-z0-9]{20,}|\$argon2(?:id|i|d)\$|\$[56]\$[^$\s]{1,16}\$[./A-Za-z0-9]{20,}|\b[0-9A-Fa-f]{33,}\b",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CredentialPattern();

    [GeneratedRegex(@"[A-Za-z0-9+/_-]{32,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LongRun();

    // A credential written into free text, at any length: a password, secret, token or API key followed by = or : and a
    // value, or a URL with a user name and a password before its host.
    [GeneratedRegex(
        @"\b(?:pass(?:word)?|pwd|secret|token|api[_-]?key)\s*[=:]\s*\S|\b[a-z][a-z0-9+.-]*://[^\s/:@]+:[^\s/@]+@",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex WrittenCredential();

    /// <summary>True when <paramref name="value"/> looks like a credential; a check that times out counts as one (fail closed).</summary>
    public static bool LooksLikeCredential(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            if (WrittenCredential().IsMatch(value))
            {
                return true;
            }

            // The shapes below are key material, which is never this short.
            if (value.Length < 16)
            {
                return false;
            }

            if (CredentialPattern().IsMatch(value))
            {
                return true;
            }

            foreach (Match run in LongRun().Matches(value))
            {
                if (IsMixedRun(run.ValueSpan) && !IsWordRun(run.Value))
                {
                    return true;
                }
            }

            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    /// <summary>True when any of <paramref name="values"/> looks like a credential.</summary>
    public static bool ContainsCredential(IEnumerable<string> values)
    {
        return values.Any(LooksLikeCredential);
    }

    // Short words joined by separators — best-selling-Product-2024-XL, Customer_Ref_2024_Q3, /var/data/Archive2024 — read as
    // a slug, SKU or path; key material does not split into short words.
    private static bool IsWordRun(string run)
    {
        var words = run.Split(SlugSeparators, StringSplitOptions.RemoveEmptyEntries);

        return words.Length > 1 && words.All(x => x.Length <= MaxSlugWordLength);
    }

    // A base64-like run reads as key material when it mixes upper case, lower case and digits — an ordinary long word or
    // a run of one letter case does not.
    private static bool IsMixedRun(ReadOnlySpan<char> run)
    {
        if (run.Length < MinRunLength)
        {
            return false;
        }

        var upper = false;
        var lower = false;
        var digit = false;
        foreach (var x in run)
        {
            upper |= char.IsAsciiLetterUpper(x);
            lower |= char.IsAsciiLetterLower(x);
            digit |= char.IsAsciiDigit(x);
        }

        return upper && lower && digit;
    }
}
