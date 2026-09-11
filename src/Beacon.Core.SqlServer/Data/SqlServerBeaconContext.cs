using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.SqlServer.Data;

internal sealed class SqlServerBeaconContext(DbContextOptions<SqlServerBeaconContext> options)
    : BeaconContext(options)
{
}
