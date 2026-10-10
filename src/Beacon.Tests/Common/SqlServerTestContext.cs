using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Tests.Common;

/// <summary>
/// A concrete BeaconContext on the SQL Server provider for query translation tests, the counterpart of
/// <see cref="NpgsqlTestContext"/>. Uses a dummy connection string: queries are checked with <c>ToQueryString()</c>,
/// which needs only the provider's SQL generator; no connection is ever opened.
/// </summary>
public sealed class SqlServerTestContext : BeaconContext
{
    public SqlServerTestContext(DbContextOptions<SqlServerTestContext> options)
        : base(options, "beacon")
    {
    }

    public static SqlServerTestContext Create()
    {
        var options = new DbContextOptionsBuilder<SqlServerTestContext>()
            .UseSqlServer("Server=localhost;Database=test_does_not_exist;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        return new SqlServerTestContext(options);
    }
}
