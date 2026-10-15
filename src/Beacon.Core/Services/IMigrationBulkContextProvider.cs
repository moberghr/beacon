using System.Data.Common;
using Beacon.Core.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Services;

/// <summary>
/// Configures the EF Core provider that a migration job's bulk writes go through for one destination engine,
/// for engines whose EF provider Beacon.Core does not reference itself. A connector package registers one
/// (MySQL: <c>AddMySqlConnector()</c>), which keeps that provider's EF dependency out of every host that never
/// writes to that engine.
/// </summary>
public interface IMigrationBulkContextProvider
{
    DatabaseEngineType EngineType { get; }

    /// <summary>Points <paramref name="optionsBuilder"/> at the already-open destination <paramref name="connection"/>.</summary>
    void Configure(DbContextOptionsBuilder optionsBuilder, DbConnection connection);
}
