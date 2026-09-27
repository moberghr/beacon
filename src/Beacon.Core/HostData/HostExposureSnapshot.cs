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
    public HostExposurePolicy(
        DatabaseEngineType engine,
        string defaultSchema,
        IReadOnlyList<HostExposedTable> tables,
        IReadOnlySet<string>? allowedFunctions = null)
    {
        Engine = engine;
        DefaultSchema = defaultSchema;
        Tables = tables;
        AllowedFunctions = allowedFunctions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public DatabaseEngineType Engine { get; }

    /// <summary>
    /// The model's default schema (dbo / public, or the model default). Informational only: SQL against a host data
    /// source must schema-qualify every table, because the connection's real default schema / search_path may differ.
    /// </summary>
    public string DefaultSchema { get; }

    public IReadOnlyList<HostExposedTable> Tables { get; }

    /// <summary>Host-approved functions allowed on top of the built-in allow-list (<see cref="HostSqlFunctions"/>).</summary>
    public IReadOnlySet<string> AllowedFunctions { get; }

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
