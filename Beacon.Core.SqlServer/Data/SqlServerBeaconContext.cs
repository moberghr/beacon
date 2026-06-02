using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.SqlServer.Data;

internal sealed class SqlServerBeaconContext(
    DbContextOptions<SqlServerBeaconContext> options,
    string defaultSchema = "beacon")
    : BeaconContext(options, defaultSchema)
{
}
