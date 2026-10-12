using Beacon.Core.Data.Enums;

namespace Beacon.Core.HostData;

/// <summary>What masking a result read from a host data source did.</summary>
/// <param name="Discarded">The result's columns did not line up with the statement's output columns, so none of it may be
/// returned.</param>
/// <param name="ExpectedColumns">How many output columns the host policy derived for the statement.</param>
/// <param name="ActualColumns">How many columns the result has.</param>
/// <param name="RenamedColumns">Result columns not named as the host policy derived them, masked whatever the policy
/// said of them.</param>
internal sealed record HostMaskOutcome(bool Discarded, int ExpectedColumns, int ActualColumns, int RenamedColumns)
{
    public static readonly HostMaskOutcome Unchanged = new(false, 0, 0, 0);
}

/// <summary>
/// Masks a result read from a host data source BY OUTPUT POSITION: the value in column <c>i</c> of a row is masked when
/// the host policy found a masked value at output position <c>i</c> of the statement (<see cref="HostPolicyResult.OutputColumns"/>),
/// whatever the database named the column. Names are only a consistency check, and it fails closed: a column whose name
/// is not the one the policy derived is masked too, and a result whose column count differs from the policy's is
/// discarded. A masked value is replaced FULLY with <see cref="MaskedValue"/> — unlike the PII guardrail's partial
/// masking, no character of it is returned; null stays null.
/// </summary>
internal static class HostResultMasker
{
    /// <summary>What a host-masked value is replaced with.</summary>
    public const string MaskedValue = "***";

    /// <param name="check">The host policy's verdict on the exact statement that produced the result.</param>
    /// <param name="hostManaged">Whether the result comes from a host data source; an ordinary source's is left as it is.</param>
    /// <param name="columns">The result's column names in reader order, a repeated name repeated.</param>
    /// <param name="rows">The rows' values in the same order; masked in place.</param>
    public static HostMaskOutcome Mask(HostPolicyResult check, bool hostManaged, IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        if (!hostManaged || rows.Count == 0)
        {
            return HostMaskOutcome.Unchanged;
        }

        var outputs = check.OutputColumns;
        if (outputs == null || outputs.Count != columns.Count || rows.Any(x => x.Length != columns.Count))
        {
            return new HostMaskOutcome(true, outputs?.Count ?? 0, columns.Count, 0);
        }

        // PostgreSQL labels a column exactly as the policy derived it (identifiers it would rename are refused); SQL Server
        // identifiers are case-insensitive, so an unaliased column may come back in another case there.
        var comparison = check.Engine == DatabaseEngineType.PostgreSQL ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var masked = new bool[columns.Count];
        var renamed = 0;
        for (var i = 0; i < columns.Count; i++)
        {
            var nameDiffers = outputs[i].Name != null && !string.Equals(outputs[i].Name, columns[i], comparison);
            if (nameDiffers)
            {
                renamed++;
            }

            masked[i] = outputs[i].Masked || nameDiffers;
        }

        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                if (masked[i] && row[i] != null)
                {
                    row[i] = MaskedValue;
                }
            }
        }

        return new HostMaskOutcome(false, outputs.Count, columns.Count, renamed);
    }
}
