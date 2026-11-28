using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Beacon.Core.Services.Metadata;

/// <summary>
/// The structure of a table as a description item records it (<see cref="Data.Entities.McpDescriptionItem.StructureHash"/>):
/// SHA-256, lowercase hex, of its column names and data types in ordinal order. Each name and type is written with its
/// length in front (<c>4:name4:text</c>), so no name or type can be read as part of another; names and types are compared
/// as written (ordinal), and the lengths are formatted culture-invariant. A refresh compares the hash of the table it
/// stores with the one recorded when the description was written.
/// </summary>
internal static class DescriptionStructureHash
{
    /// <summary>The hash of <paramref name="columnsInOrdinalOrder"/>, taken in the order given.</summary>
    public static string Of(IEnumerable<(string Name, string DataType)> columnsInOrdinalOrder)
    {
        var canonical = new StringBuilder();
        foreach (var (name, dataType) in columnsInOrdinalOrder)
        {
            Append(canonical, name);
            Append(canonical, dataType);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    /// <summary>The hash of <paramref name="columns"/> put in ordinal order (by position, then by name).</summary>
    public static string Of(IEnumerable<(string Name, string DataType, int OrdinalPosition)> columns)
    {
        return Of(columns
            .OrderBy(x => x.OrdinalPosition)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => (x.Name, x.DataType)));
    }

    private static void Append(StringBuilder canonical, string value)
    {
        canonical
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);
    }
}
