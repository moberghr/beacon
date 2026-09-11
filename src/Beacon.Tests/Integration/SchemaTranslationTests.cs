using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Beacon.Core.Data;

namespace Beacon.Tests.Integration;

/// <summary>
/// Verifies that PostgreSQL query translation reflects the configured schema (§4.3/§4.4 —
/// ToQueryString() against a dummy connection string, no real database).
///
/// PostgreSQL previously never called HasDefaultSchema — every generated name was
/// unqualified and the schema was applied only via SearchPath on the connection. B3 moves it
/// to full symmetry with SQL Server: with a configured schema, the model gains
/// HasDefaultSchema and generated SQL becomes schema-qualified; with no configured schema and
/// an empty ctor default, the legacy unqualified behaviour is pinned so the
/// IsNullOrEmpty guard in BeaconContext.OnModelCreating cannot silently rot.
///
/// NpgsqlTestContext (Common/NpgsqlTestContext.cs) is out of this batch's scope and is not
/// used here — it hardcodes "beacon" as its ctor schema, which is not what these two cases
/// need to pin. Local test contexts are defined below instead.
/// </summary>
[TestFixture]
public class SchemaTranslationTests
{
    [Test]
    public void PostgreSqlContext_WithConfiguredSchema_QualifiesTables_Translates()
    {
        using var context = SchemaConfiguredTestContext.Create("tenant_a");

        var sql = context.Projects.ToQueryString();

        sql.Should().Contain("tenant_a.projects");
        sql.Should().NotContain("beacon");
    }

    [Test]
    public void PostgreSqlContext_WithEmptySchema_EmitsUnqualified_Translates()
    {
        using var context = EmptySchemaTestContext.Create();

        var sql = context.Projects.ToQueryString();

        sql.Should().Contain("FROM projects");
        sql.Should().NotContain(".projects");
    }

    private sealed class SchemaConfiguredTestContext : BeaconContext
    {
        public SchemaConfiguredTestContext(DbContextOptions<SchemaConfiguredTestContext> options)
            : base(options, "beacon")
        {
        }

        public static SchemaConfiguredTestContext Create(string schema)
        {
            var builder = new DbContextOptionsBuilder<SchemaConfiguredTestContext>()
                .UseNpgsql("Host=localhost;Database=test_does_not_exist")
                .UseSnakeCaseNamingConvention();

            builder.UseBeaconSchema(schema);

            return new SchemaConfiguredTestContext(builder.Options);
        }
    }

    private sealed class EmptySchemaTestContext : BeaconContext
    {
        public EmptySchemaTestContext(DbContextOptions<EmptySchemaTestContext> options)
            : base(options, string.Empty)
        {
        }

        public static EmptySchemaTestContext Create()
        {
            var options = new DbContextOptionsBuilder<EmptySchemaTestContext>()
                .UseNpgsql("Host=localhost;Database=test_does_not_exist")
                .UseSnakeCaseNamingConvention()
                .Options;

            return new EmptySchemaTestContext(options);
        }
    }
}
