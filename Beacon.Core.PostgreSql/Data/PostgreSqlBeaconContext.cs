using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.PostgreSql.Data;

internal sealed class PostgreSqlBeaconContext(
    DbContextOptions<PostgreSqlBeaconContext> options,
    string defaultSchema = "beacon")
    : BeaconContext(options, defaultSchema)
{
}
