using System.Text.RegularExpressions;

namespace Beacon.AI.Services.Embeddings;

/// <summary>
/// DAIL-SQL style question masking. Strips literals so that two questions differing only by their
/// values (numbers, quoted strings, dates) collapse to the same masked form. Used at exemplar index
/// time and at query time so semantic exemplar similarity keys on question structure, not on values.
/// Deterministic and pure.
/// </summary>
public static class EmbeddingMaskingHelper
{
    private static readonly Regex SingleQuoted = new("'[^']*'", RegexOptions.Compiled);
    private static readonly Regex DoubleQuoted = new("\"[^\"]*\"", RegexOptions.Compiled);

    // ISO (2023-05-01, 2023/5/1) and slash dates (05/01/2023, 5/1/23) — masked before numbers so the
    // digit groups inside a date are not individually turned into <num>.
    private static readonly Regex DateLike = new(
        @"\b\d{4}[-/]\d{1,2}[-/]\d{1,2}\b|\b\d{1,2}[-/]\d{1,2}[-/]\d{2,4}\b",
        RegexOptions.Compiled);

    private static readonly Regex Number = new(@"\d+(?:[.,]\d+)*", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Mask(string question)
    {
        if (string.IsNullOrEmpty(question))
        {
            return string.Empty;
        }

        var masked = question.ToLowerInvariant();
        masked = SingleQuoted.Replace(masked, "<value>");
        masked = DoubleQuoted.Replace(masked, "<value>");
        masked = DateLike.Replace(masked, "<value>");
        masked = Number.Replace(masked, "<num>");
        masked = Whitespace.Replace(masked, " ").Trim();
        return masked;
    }
}
