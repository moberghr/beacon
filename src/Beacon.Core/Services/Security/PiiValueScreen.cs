using System.Text.RegularExpressions;

namespace Beacon.Core.Services.Security;

/// <summary>
/// Value-shaped PII screen: email addresses, SSN-like, credit-card-like, phone-like patterns
/// (§1.6 — a benignly named column can still hold PII values). Extracted from
/// <c>ColumnValueSampler</c> so <c>Beacon.AI</c>'s value-grounding probes can screen results too,
/// without a reference into the internal Core sampler type.
/// </summary>
public static partial class PiiValueScreen
{
    [GeneratedRegex(
        @"([A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})|(\b\d{3}-\d{2}-\d{4}\b)|(\b(?:\d[ -]?){13,19}\b)|(\+?\d{1,3}[ .-]?\(?\d{2,4}\)?[ .-]?\d{3}[ .-]?\d{2,4})",
        RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PiiValuePattern();

    /// <summary>
    /// Returns true when any value looks PII-shaped (built-in regex or a custom pattern), or when a
    /// custom pattern fails to evaluate (fail closed — an unusable pattern means we can't clear the
    /// value, so treat it as PII).
    /// </summary>
    public static bool ContainsPiiValue(IReadOnlyList<string> values, IReadOnlyList<string>? customPatterns)
    {
        foreach (var value in values)
        {
            try
            {
                if (PiiValuePattern().IsMatch(value))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Treat a timeout as a match — safer to drop the samples than risk leaking PII
                return true;
            }

            if (customPatterns is not { Count: > 0 })
            {
                continue;
            }

            foreach (var pattern in customPatterns)
            {
                try
                {
                    if (Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
                {
                    // Fail CLOSED: an unusable custom pattern means we can't clear this value, so
                    // treat it as PII and drop the sample rather than risk persisting a leak.
                    return true;
                }
            }
        }

        return false;
    }
}
