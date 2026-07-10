using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Beacon.AI.Services.Eval;

/// <summary>
/// Pure, order-independent fingerprint of a query result set for execution-accuracy (EX) comparison.
/// Two result sets that contain the same rows (as a multiset) with the same columns produce the same
/// string, regardless of row order; a differing cell value, an extra/missing row, or a differing
/// column produces a different string. Column names are canonicalized (ordinal-sorted) and cell values
/// normalized with invariant formatting so the comparison is culture- and provider-stable. No database,
/// LLM, or I/O — fully unit-testable.
/// </summary>
internal static class ResultSetFingerprint
{
    // Unit/record separators keep column/row boundaries unambiguous inside the hashed payload.
    private const char CellSeparator = '\u001f';
    private const char RowSeparator = '\u001e';
    private const string NullToken = "\0NULL";

    /// <summary>
    /// Computes a stable fingerprint for the given rows. An empty or null set maps to a fixed
    /// "no rows" token so two empty result sets compare equal.
    /// </summary>
    public static string Compute(IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows)
    {
        if (rows == null || rows.Count == 0)
        {
            return "cols=0;rows=0;";
        }

        var columns = rows
            .SelectMany(x => x.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var rowStrings = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            var builder = new StringBuilder();
            foreach (var column in columns)
            {
                row.TryGetValue(column, out var value);
                builder.Append(column).Append('=').Append(NormalizeValue(value)).Append(CellSeparator);
            }

            rowStrings.Add(builder.ToString());
        }

        // Multiset semantics: sort the per-row canonical strings so row order does not change the hash.
        rowStrings.Sort(StringComparer.Ordinal);

        var payload = string.Join(",", columns) + "\n" + string.Join(RowSeparator, rowStrings);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));

        return $"cols={columns.Count};rows={rows.Count};{Convert.ToHexString(hash)}";
    }

    private static string NormalizeValue(object? value)
    {
        if (value is null or DBNull)
        {
            return NullToken;
        }

        return value switch
        {
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? NullToken
        };
    }
}
