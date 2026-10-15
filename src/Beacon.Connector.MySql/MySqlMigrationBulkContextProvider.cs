using System.Data.Common;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Connector.MySql;

/// <summary>
/// The Pomelo EF Core provider for migration jobs that bulk-write into MySQL. It lives here rather than in
/// Beacon.Core because Pomelo 9.0.0 constrains EF Core to 9.x: every host referencing Beacon.Core inherited that
/// NU1608 warning, which is an error under TreatWarningsAsErrors. Only hosts that add this connector carry it now.
/// </summary>
internal sealed class MySqlMigrationBulkContextProvider : IMigrationBulkContextProvider
{
    public DatabaseEngineType EngineType => DatabaseEngineType.MySQL;

    public void Configure(DbContextOptionsBuilder optionsBuilder, DbConnection connection)
    {
        optionsBuilder.UseMySql(connection, ServerVersion.AutoDetect(connection.ConnectionString));
    }
}
