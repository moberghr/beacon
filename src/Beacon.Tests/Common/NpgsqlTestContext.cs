using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Tests.Common;

/// <summary>
/// A concrete BeaconContext configured with the Npgsql provider for query translation testing.
/// Uses a dummy connection string — no actual database connection is made.
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
            .Options;

        return new NpgsqlTestContext(options);
    }
}
