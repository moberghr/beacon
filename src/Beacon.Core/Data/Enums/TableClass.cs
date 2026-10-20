namespace Beacon.Core.Data.Enums;

/// <summary>
/// A table's class for row security. On a data source with a Protected table, a table without a class is refused.
/// </summary>
public enum TableClass
{
    /// <summary>Every row is readable by whoever may read the data source.</summary>
    Open = 1,

    /// <summary>Rows are readable only through the table's active row policies; with none, no row is.</summary>
    Protected = 2
}
