using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Tests.Common;

/// <summary>
/// A concrete BeaconContext configured with the Npgsql provider for query translation testing.
/// Uses a dummy connection string and never opens it (<see cref="SqlCapture.NeverConnects"/>): a query executed by
/// mistake fails at once instead of reaching a server on localhost.
/// Queries are validated via ToQueryString() which only requires the provider's SQL generator.
/// </summary>
public sealed class NpgsqlTestContext : BeaconContext
{
    public NpgsqlTestContext(DbContextOptions<NpgsqlTestContext> options)
        : base(options, "beacon")
    {
    }

    public static NpgsqlTestContext Create()
    {
        var options = new DbContextOptionsBuilder<NpgsqlTestContext>()
            .UseNpgsql("Host=localhost;Database=test_does_not_exist")
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(SqlCapture.NeverConnects)
            .Options;

        return new NpgsqlTestContext(options);
    }
}
