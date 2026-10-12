using System.Text.RegularExpressions;

namespace Beacon.Core.Services.Security;

/// <summary>
/// Banking and national identifiers recognised by their check digits, so a value is recognised by what it is rather than
/// by its length alone: an IBAN (ISO 13616, mod 97), a Croatian OIB (ISO 7064 MOD 11,10) and a JMBG (its weighted mod 11
/// check digit and a plausible day and month). <see cref="PiiValueScreen"/> treats a value holding any of them as PII.
/// </summary>
public static partial class BankingIdentifiers
{
    [GeneratedRegex(@"\b(?<head>[A-Za-z]{2}\d{2})(?<part> ?[A-Za-z0-9]{1,4}){3,8}\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IbanCandidate();

    [GeneratedRegex(@"(?<!\d)\d{11}(?!\d)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ElevenDigits();

    [GeneratedRegex(@"(?<!\d)\d{13}(?!\d)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ThirteenDigits();

    private static readonly int[] JmbgWeights = [7, 6, 5, 4, 3, 2, 7, 6, 5, 4, 3, 2];

    /// <summary>True when <paramref name="value"/> holds an IBAN, an OIB or a JMBG whose check digits are right.</summary>
    public static bool ContainsBankingIdentifier(string value)
    {
        foreach (Match candidate in IbanCandidate().Matches(value))
        {
            if (HoldsIban(candidate))
            {
                return true;
            }
        }

        foreach (Match candidate in ElevenDigits().Matches(value))
        {
            if (IsOib(candidate.Value))
            {
                return true;
            }
        }

        foreach (Match candidate in ThirteenDigits().Matches(value))
        {
            if (IsJmbg(candidate.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>An IBAN, printed with or without spaces: a country code, two check digits and 11 to 30 letters or digits, mod 97 = 1.</summary>
    public static bool IsIban(string value)
    {
        var compact = value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (compact.Length is < 15 or > 34)
        {
            return false;
        }

        if (!char.IsAsciiLetterUpper(compact[0]) || !char.IsAsciiLetterUpper(compact[1]) || !char.IsAsciiDigit(compact[2]) || !char.IsAsciiDigit(compact[3]))
        {
            return false;
        }

        var rearranged = compact[4..] + compact[..4];
        var remainder = 0;
        foreach (var x in rearranged)
        {
            if (char.IsAsciiDigit(x))
            {
                remainder = ((remainder * 10) + (x - '0')) % 97;
                continue;
            }

            if (!char.IsAsciiLetterUpper(x))
            {
                return false;
            }

            remainder = ((remainder * 100) + (x - 'A' + 10)) % 97;
        }

        return remainder == 1;
    }

    /// <summary>A Croatian personal identification number (OIB): 11 digits whose last is the ISO 7064 MOD 11,10 check digit.</summary>
    public static bool IsOib(string value)
    {
        if (value.Length != 11 || !value.All(char.IsAsciiDigit))
        {
            return false;
        }

        var product = 10;
        for (var i = 0; i < 10; i++)
        {
            var sum = (product + (value[i] - '0')) % 10;
            product = ((sum == 0 ? 10 : sum) * 2) % 11;
        }

        var check = 11 - product;

        return (check == 10 ? 0 : check) == value[10] - '0';
    }

    /// <summary>
    /// A unique master citizen number (JMBG): 13 digits, a day from 01 to 31 and a month from 01 to 12, and the weighted
    /// mod 11 check digit last (a remainder of 0 or 1 gives 0).
    /// </summary>
    public static bool IsJmbg(string value)
    {
        if (value.Length != 13 || !value.All(char.IsAsciiDigit))
        {
            return false;
        }

        var day = ((value[0] - '0') * 10) + (value[1] - '0');
        var month = ((value[2] - '0') * 10) + (value[3] - '0');
        if (day is < 1 or > 31 || month is < 1 or > 12)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            sum += JmbgWeights[i] * (value[i] - '0');
        }

        var check = 11 - (sum % 11);

        return (check > 9 ? 0 : check) == value[12] - '0';
    }

    // A candidate can run on into the words after the IBAN ("HR12 1001 … 0 is mine"): its head with its first parts, the
    // longest first, is checked down to the shortest an IBAN can be.
    private static bool HoldsIban(Match candidate)
    {
        var parts = candidate.Groups["part"].Captures;
        for (var count = parts.Count; count >= 3; count--)
        {
            var text = candidate.Groups["head"].Value + string.Concat(parts.Take(count).Select(x => x.Value));
            if (IsIban(text))
            {
                return true;
            }
        }

        return false;
    }
}
