using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Beacon.Core.Services;

/// <summary>
/// What a curator reviewed, as a SHA-256 (lowercase hex): the lists hand it out with each row, and a decision sends it
/// back, so a decision applies only to the content the curator saw — content that changed in between is refused (409),
/// never decided unseen. Hashed character for character: whitespace, line endings and comments included. The same digests
/// are stored with the content (<see cref="Data.Entities.McpLearnedPattern.ContentHash"/>,
/// <see cref="Data.Entities.McpQuerySignal.CandidateSqlHash"/>), so a conditional write compares them instead of the text:
/// fixed-length lowercase hex compares exactly under any database collation, where text equality may ignore case or
/// trailing spaces.
/// </summary>
public static class GroundingContentHash
{
    /// <summary>The hash of a golden candidate's SQL — the SQL a promotion starts from.</summary>
    public static string OfSql(string sql)
    {
        return Hash(sql);
    }

    /// <summary>
    /// The <see cref="Data.Entities.McpQuerySignal.CandidateSqlHash"/> of a signal holding this SQL: the hash of
    /// <paramref name="userCorrectedSql"/>, else <paramref name="correctedSql"/>, else <paramref name="generatedSql"/>; null
    /// when it holds none.
    /// </summary>
    public static string? OfCandidateSql(string? userCorrectedSql, string? correctedSql, string? generatedSql)
    {
        var sql = userCorrectedSql ?? correctedSql ?? generatedSql;

        return sql == null ? null : Hash(sql);
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
