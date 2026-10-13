using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Beacon.Core.Services;

/// <summary>
/// What a curator reviewed, as a SHA-256 (lowercase hex): the lists hand it out with each row, and a decision sends it
/// back, so a decision applies only to the content the curator saw — content that changed in between is refused (409),
/// never decided unseen. Hashed character for character: whitespace, line endings and comments included.
/// </summary>
internal static class GroundingContentHash
{
    /// <summary>The hash of a golden candidate's SQL — the SQL a promotion starts from.</summary>
    public static string OfSql(string sql)
    {
        return Hash(sql);
    }

    /// <summary>
    /// The hash of a learned pattern's reviewable content: its lesson, example question and example SQL. A missing example
    /// and an empty one hash differently, as a reviewer sees them differently.
    /// </summary>
    public static string OfPattern(string patternContent, string? exampleQuestion, string? exampleSql)
    {
        return Hash(JsonSerializer.Serialize(new[] { patternContent, exampleQuestion, exampleSql }));
    }

    /// <summary>Whether <paramref name="reviewed"/> is <paramref name="current"/>, ignoring the hex digits' case.</summary>
    public static bool Matches(string? reviewed, string current)
    {
        return reviewed != null && string.Equals(reviewed.Trim(), current, StringComparison.OrdinalIgnoreCase);
    }

    private static string Hash(string text)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
