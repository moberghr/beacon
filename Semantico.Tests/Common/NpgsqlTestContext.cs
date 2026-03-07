using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;

namespace Semantico.Tests.Common;

/// <summary>
/// A concrete SemanticoContext configured with the Npgsql provider for query translation testing.
/// Uses a dummy connection string — no actual database connection is made.
/// Queries are validated via ToQueryString() which only requires the provider's SQL generator.
/// </summary>
public sealed class NpgsqlTestContext : SemanticoContext
{
    public NpgsqlTestContext(DbContextOptions<NpgsqlTestContext> options)
        : base(options, "semantico")
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
