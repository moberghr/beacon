using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Beacon.Core.Services.Security;

/// <summary>
/// An administrator's custom PII pattern, as every check that runs one builds it: case-insensitive, on the linear-time
/// engine (<see cref="RegexOptions.NonBacktracking"/>) when the pattern can be built for it, otherwise (lookarounds,
/// backreferences, atomic groups — only a pattern stored before the save refused them) on the backtracking engine under
/// <see cref="MatchTimeout"/>. A caller reads a timeout or an invalid pattern as a match (fail closed). Built patterns are
/// cached by their text, the engine choice included.
/// </summary>
internal static class CustomPiiPattern
{
    /// <summary>The longest custom pattern a save accepts.</summary>
    public const int MaxLength = 500;

    /// <summary>The refusal of a new pattern the linear-time engine cannot build.</summary>
    public const string UnsupportedConstructMessage = "Custom PII patterns cannot use lookarounds, backreferences or atomic groups.";

    /// <summary>The most patterns cached; past it the cache starts again.</summary>
    internal const int CacheLimit = 256;

    /// <summary>How long one match may run on the backtracking engine before it counts as a match.</summary>
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    /// <summary>The cached regex of <paramref name="pattern"/>; an invalid pattern throws <see cref="ArgumentException"/>.</summary>
    public static Regex Get(string pattern)
    {
        if (Cache.TryGetValue(pattern, out var cached))
        {
            return cached;
        }

        var regex = Build(pattern);
        if (Cache.Count >= CacheLimit)
        {
            Cache.Clear();
        }

        return Cache.GetOrAdd(pattern, regex);
    }

    /// <summary>
    /// Refuses (<see cref="InvalidOperationException"/>) a pattern a save adds: one in <paramref name="incoming"/> and not
    /// in <paramref name="stored"/> that is longer than <see cref="MaxLength"/>, not a valid regular expression, or one the
    /// linear-time engine cannot build. A pattern already stored is not checked again, so it never blocks an unrelated save.
    /// </summary>
    public static void CheckAdded(IEnumerable<string> incoming, IEnumerable<string> stored)
    {
        foreach (var pattern in incoming.Except(stored, StringComparer.Ordinal))
        {
            if (pattern.Length > MaxLength)
            {
                throw new InvalidOperationException($"Custom PII patterns must be at most {MaxLength} characters.");
            }

            try
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
            }
            catch (NotSupportedException)
            {
                throw new InvalidOperationException(UnsupportedConstructMessage);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Custom PII pattern is not a valid regular expression: {ex.Message}");
            }
        }
    }

    /// <summary>The patterns a settings row stores (a JSON array); none when it holds none or cannot be read.</summary>
    public static IReadOnlyList<string> Stored(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Regex Build(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, MatchTimeout);
        }
        catch (NotSupportedException)
        {
            return new Regex(pattern, RegexOptions.IgnoreCase, MatchTimeout);
        }
    }
}
