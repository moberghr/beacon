using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Metadata;

namespace Beacon.Core.HostData;

/// <summary>
/// The exposed slice of one host EF model: the metadata Beacon persists (allow-listed tables, non-excluded
/// columns), the execution policy that enforces it, and a hash of both used to skip unchanged syncs.
/// </summary>
internal sealed record HostExposureSnapshot(
    DatabaseEngineType Engine,
    IReadOnlyList<TableMetadataDto> Tables,
    HostExposurePolicy Policy,
    string ModelHash);

/// <summary>
/// What SQL against a host-managed data source may touch. Built from the EF model, never from user input.
/// </summary>
internal sealed class HostExposurePolicy
{
    public HostExposurePolicy(DatabaseEngineType engine, string defaultSchema, IReadOnlyList<HostExposedTable> tables)
    {
        Engine = engine;
        DefaultSchema = defaultSchema;
        Tables = tables;
    }

    public DatabaseEngineType Engine { get; }

    /// <summary>Schema an unqualified table name resolves to (dbo / public, or the model default).</summary>
    public string DefaultSchema { get; }

    public IReadOnlyList<HostExposedTable> Tables { get; }

    /// <summary>A policy that exposes nothing — used when a host data source has no live registration.</summary>
    public static HostExposurePolicy DenyAll(DatabaseEngineType engine)
    {
        return new HostExposurePolicy(engine, engine == DatabaseEngineType.MSSQL ? "dbo" : "public", []);
    }
}

/// <summary>One allow-listed table or view with its column partition.</summary>
internal sealed record HostExposedTable(
    string Schema,
    string Name,
    IReadOnlySet<string> Columns,
    IReadOnlySet<string> ExcludedColumns,
    IReadOnlySet<string> MaskedColumns);
