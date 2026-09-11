using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.PostgreSql;
using Beacon.Core.SqlServer;
using Beacon.Core.Worker;

namespace Beacon.Tests.Unit;

[TestFixture]
public class SchemaConfigurationTests
{
    [Test]
    public void UseBeaconSchema_RoundTripsThroughOptions()
    {
        var builder = new DbContextOptionsBuilder();

        builder.UseBeaconSchema("tenant_a");
        var options = builder.Options;

        var extension = options.FindExtension<BeaconSchemaOptionsExtension>();

        extension.Should().NotBeNull();
        extension!.Schema.Should().Be("tenant_a");
    }

    [Test]
    public void CreateSchemaStatement_PostgreSql_IsIdempotent()
    {
        var statement = BeaconSchema.CreateSchemaStatement("Npgsql.EntityFrameworkCore.PostgreSQL", "beacon");

        statement.Should().Contain("IF NOT EXISTS");
        statement.Should().Contain("\"beacon\"");
    }

    [Test]
    public void CreateSchemaStatement_SqlServer_IsIdempotent()
    {
        var statement = BeaconSchema.CreateSchemaStatement("Microsoft.EntityFrameworkCore.SqlServer", "beacon");

        statement.Should().Contain("sys.schemas");
        statement.Should().Contain("EXEC('CREATE SCHEMA [beacon]')");
        statement.Should().StartWith("IF NOT EXISTS");
        statement.Should().NotStartWith("CREATE SCHEMA");
    }

    [Test]
    public void CreateSchemaStatement_InvalidIdentifier_Throws()
    {
        const string injection = "a; DROP TABLE x--";

        var act = () => BeaconSchema.CreateSchemaStatement("Npgsql.EntityFrameworkCore.PostgreSQL", injection);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{injection}*");
    }

    // SchemaAwareMigrationsSqlGenerator and SchemaModelCacheKeyFactory are internal to
    // Beacon.Core.SqlServer, which grants InternalsVisibleTo to no test project, so these
    // tests drive them the only way this assembly can: resolve the replaced services from a
    // real DI container built via the public UseSqlServer() wiring, exactly as the host does.

    [Test]
    public void SqlServerGenerator_RetargetsCreateTableSchema()
    {
        using var provider = BuildSqlServerProvider("tenant_a");
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var createTable = new CreateTableOperation
        {
            Name = "widgets",
            Schema = "beacon",
            Columns =
            {
                new AddColumnOperation
                {
                    Name = "id",
                    Schema = "beacon",
                    Table = "widgets",
                    ClrType = typeof(int),
                    ColumnType = "int"
                }
            }
        };

        var commands = generator.Generate([createTable]);
        var sql = string.Join(Environment.NewLine, commands.Select(x => x.CommandText));

        sql.Should().Contain("[tenant_a].[widgets]");
        sql.Should().NotContain("[beacon].[widgets]");
    }

    [Test]
    public void SqlServerGenerator_RetargetsForeignKeyPrincipalSchema()
    {
        using var provider = BuildSqlServerProvider("tenant_a");
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var addForeignKey = new AddForeignKeyOperation
        {
            Name = "fk_widgets_categories",
            Table = "widgets",
            Schema = "beacon",
            Columns = ["category_id"],
            PrincipalTable = "categories",
            PrincipalSchema = "beacon",
            PrincipalColumns = ["id"]
        };

        var commands = generator.Generate([addForeignKey]);
        var sql = string.Join(Environment.NewLine, commands.Select(x => x.CommandText));

        sql.Should().Contain("[tenant_a].[categories]");
        sql.Should().NotContain("[beacon].[categories]");
    }

    [Test]
    public void ModelCacheKeyFactory_DiffersBySchema()
    {
        using var providerA = BuildSqlServerProvider("tenant_a");
        using var providerB = BuildSqlServerProvider("tenant_b");
        using var contextA = providerA.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        using var contextB = providerB.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();

        var keyA = contextA.GetService<IModelCacheKeyFactory>().Create(contextA, designTime: false);
        var keyB = contextB.GetService<IModelCacheKeyFactory>().Create(contextB, designTime: false);

        keyA.Should().NotBe(keyB);
    }

    [Test]
    public void ModelCacheKeyFactory_EqualForSameSchema()
    {
        using var providerA = BuildSqlServerProvider("tenant_a");
        using var providerB = BuildSqlServerProvider("tenant_a");
        using var contextA = providerA.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        using var contextB = providerB.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();

        var keyA = contextA.GetService<IModelCacheKeyFactory>().Create(contextA, designTime: false);
        var keyB = contextB.GetService<IModelCacheKeyFactory>().Create(contextB, designTime: false);

        keyA.Should().Be(keyB);
    }

    // PostgreSql-specific: unlike SQL Server, this project's committed migrations carry no
    // schema argument at all (Schema = null), so retargeting null is the load-bearing case
    // for PostgreSQL — see SchemaAwareMigrationsSqlGenerator's XML doc.

    [Test]
    public void PostgreSqlGenerator_RetargetsNullSchema()
    {
        using var provider = BuildPostgreSqlProvider("tenant_a");
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var createTable = new CreateTableOperation
        {
            Name = "widgets",
            Schema = null,
            Columns =
            {
                new AddColumnOperation
                {
                    Name = "id",
                    Schema = null,
                    Table = "widgets",
                    ClrType = typeof(int),
                    ColumnType = "int"
                }
            }
        };

        var commands = generator.Generate([createTable]);
        var sql = string.Join(Environment.NewLine, commands.Select(x => x.CommandText));

        sql.Should().Contain("tenant_a.widgets");
        sql.Should().NotContain("beacon.widgets");
    }

    [Test]
    public void PostgreSqlGenerator_RetargetsForeignKeyPrincipalSchema()
    {
        using var provider = BuildPostgreSqlProvider("tenant_a");
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var addForeignKey = new AddForeignKeyOperation
        {
            Name = "fk_widgets_categories",
            Table = "widgets",
            Schema = "beacon",
            Columns = ["category_id"],
            PrincipalTable = "categories",
            PrincipalSchema = "beacon",
            PrincipalColumns = ["id"]
        };

        var commands = generator.Generate([addForeignKey]);
        var sql = string.Join(Environment.NewLine, commands.Select(x => x.CommandText));

        sql.Should().Contain("tenant_a.categories");
        sql.Should().NotContain("beacon.categories");
    }

    [Test]
    public void PostgreSqlGenerator_RetargetsBeaconLiteral()
    {
        using var provider = BuildPostgreSqlProvider("tenant_a");
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var createTable = new CreateTableOperation
        {
            Name = "widgets",
            Schema = "beacon",
            Columns =
            {
                new AddColumnOperation
                {
                    Name = "id",
                    Schema = "beacon",
                    Table = "widgets",
                    ClrType = typeof(int),
                    ColumnType = "int"
                }
            }
        };

        var commands = generator.Generate([createTable]);
        var sql = string.Join(Environment.NewLine, commands.Select(x => x.CommandText));

        sql.Should().Contain("tenant_a.widgets");
        sql.Should().NotContain("beacon.widgets");
    }

    // The existing SqlServerGenerator_RetargetsCreateTableSchema populates only Columns, and a
    // column's .Schema has no observable effect on CREATE TABLE DDL — that test would still
    // pass even if RetargetNestedOperations were gutted entirely. This test instead nests an
    // AddForeignKeyOperation (as the Initial migration does 40+ times) and asserts the inline
    // REFERENCES clause is retargeted.
    [Test]
    public void SqlServerGenerator_RetargetsNestedForeignKeySchema()
    {
        using var provider = BuildSqlServerProvider("tenant_a");
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var createTable = new CreateTableOperation
        {
            Name = "widgets",
            Schema = "beacon",
            Columns =
            {
                new AddColumnOperation
                {
                    Name = "id",
                    Schema = "beacon",
                    Table = "widgets",
                    ClrType = typeof(int),
                    ColumnType = "int"
                },
                new AddColumnOperation
                {
                    Name = "category_id",
                    Schema = "beacon",
                    Table = "widgets",
                    ClrType = typeof(int),
                    ColumnType = "int"
                }
            },
            ForeignKeys =
            {
                new AddForeignKeyOperation
                {
                    Name = "fk_widgets_categories",
                    Table = "widgets",
                    Schema = "beacon",
                    Columns = ["category_id"],
                    PrincipalTable = "categories",
                    PrincipalSchema = "beacon",
                    PrincipalColumns = ["id"]
                }
            }
        };

        var commands = generator.Generate([createTable]);
        var sql = string.Join(Environment.NewLine, commands.Select(x => x.CommandText));

        sql.Should().Contain("REFERENCES [tenant_a].[categories]");
        sql.Should().NotContain("REFERENCES [beacon].[categories]");
    }

    [Test]
    public void SqlServerGenerator_RetargetsEnsureSchemaOperation()
    {
        using var provider = BuildSqlServerProvider("tenant_a");
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var ensureSchema = new EnsureSchemaOperation { Name = "beacon" };

        var commands = generator.Generate([ensureSchema]);
        var sql = string.Join(Environment.NewLine, commands.Select(x => x.CommandText));

        sql.Should().Contain("tenant_a");
        sql.Should().NotContain("beacon");
    }

    [Test]
    public void SqlServerGenerator_RetargetsRenameTableNewSchema()
    {
        using var provider = BuildSqlServerProvider("tenant_a");
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var renameTable = new RenameTableOperation
        {
            Name = "widgets",
            Schema = "beacon",
            NewName = "widgets_renamed",
            NewSchema = "beacon"
        };

        var commands = generator.Generate([renameTable]);
        var sql = string.Join(Environment.NewLine, commands.Select(x => x.CommandText));

        sql.Should().Contain("tenant_a");
        sql.Should().NotContain("beacon");
    }

    [Test]
    public void Generator_ThrowsOnUnexpectedSchemaLiteral()
    {
        using var provider = BuildSqlServerProvider("tenant_a");
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var createTable = new CreateTableOperation
        {
            Name = "widgets",
            Schema = "stale_literal",
            Columns =
            {
                new AddColumnOperation
                {
                    Name = "id",
                    Schema = "stale_literal",
                    Table = "widgets",
                    ClrType = typeof(int),
                    ColumnType = "int"
                }
            }
        };

        var act = () => generator.Generate([createTable]);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("stale_literal");
        exception.Message.Should().Contain("tenant_a");
        exception.Message.Should().Contain(nameof(CreateTableOperation));
    }

    // The existing ModelCacheKeyFactory_DiffersBySchema only compares the key tuple, in a setup
    // where the collision it guards against structurally cannot occur (separate DI containers).
    // This proves the MODELS themselves differ, not just the keys.
    [Test]
    public void ModelCacheKey_DifferentSchemasBuildDifferentModels()
    {
        using var providerA = BuildSqlServerProvider("tenant_a");
        using var providerB = BuildSqlServerProvider("tenant_b");
        using var contextA = providerA.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        using var contextB = providerB.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();

        contextA.Model.GetDefaultSchema().Should().Be("tenant_a");
        contextB.Model.GetDefaultSchema().Should().Be("tenant_b");
        contextA.Model.GetDefaultSchema().Should().NotBe(contextB.Model.GetDefaultSchema());
    }

    [Test]
    public void BeaconSchemaOptionsExtension_Validate_ThrowsOnHistorySchemaMismatch()
    {
        var builder = new DbContextOptionsBuilder();

        builder.UseSqlServer(
            "Server=localhost;Database=unused;Trusted_Connection=True;TrustServerCertificate=True",
            x => x.MigrationsHistoryTable("__EFMigrationsHistory", "beacon"));
        builder.UseBeaconSchema("tenant_a");

        var extension = builder.Options.FindExtension<BeaconSchemaOptionsExtension>();

        var act = () => extension!.Validate(builder.Options);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("tenant_a");
        exception.Message.Should().Contain("beacon");
    }

    [Test]
    public void BeaconSchemaOptionsExtension_Validate_AllowsNullHistorySchema()
    {
        var builder = new DbContextOptionsBuilder();

        builder.UseSqlServer("Server=localhost;Database=unused;Trusted_Connection=True;TrustServerCertificate=True");
        builder.UseBeaconSchema("tenant_a");

        var extension = builder.Options.FindExtension<BeaconSchemaOptionsExtension>();

        var act = () => extension!.Validate(builder.Options);

        act.Should().NotThrow();
    }

    private static ServiceProvider BuildPostgreSqlProvider(string schema)
    {
        var services = new ServiceCollection();

        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = Convert.ToBase64String(new byte[32])
            })
            .Build();

        var builder = services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());

        builder.UsePostgreSql(
            "Host=localhost;Database=unused",
            schema);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static ServiceProvider BuildSqlServerProvider(string schema)
    {
        var services = new ServiceCollection();

        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = Convert.ToBase64String(new byte[32])
            })
            .Build();

        var builder = services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());

        builder.UseSqlServer(
            "Server=localhost;Database=unused;Trusted_Connection=True;TrustServerCertificate=True",
            schema);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
