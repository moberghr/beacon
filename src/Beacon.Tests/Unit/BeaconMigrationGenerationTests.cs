using System.Text.RegularExpressions;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.PostgreSql;
using Beacon.Core.SqlServer;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using PostgreSqlBeaconContext = Beacon.Core.PostgreSql.Data.PostgreSqlBeaconContext;
using SqlServerBeaconContext = Beacon.Core.SqlServer.Data.SqlServerBeaconContext;

namespace Beacon.Tests.Unit;

/// <summary>
/// Pins what Beacon's committed migrations generate on each provider, through the real <c>UsePostgreSql</c> /
/// <c>UseSqlServer</c> wiring: the model matches the latest snapshot (no pending model changes, so
/// <c>Database.Migrate()</c> applies instead of failing with PendingModelChangesWarning), and the full migration script
/// lands every table, and the migrations history table, in the <c>beacon</c> schema. No database is contacted.
/// </summary>
[TestFixture]
public class BeaconMigrationGenerationTests
{
    private const string SqlServerConnectionString = "Server=localhost;Database=unused;Trusted_Connection=True;TrustServerCertificate=True";

    private static readonly Regex PostgreSqlCreateTable = new(@"CREATE TABLE (?:IF NOT EXISTS )?(?<name>[^\s(]+)", RegexOptions.Compiled);
    private static readonly Regex SqlServerCreateTable = new(@"CREATE TABLE (?<name>[^\s(]+)", RegexOptions.Compiled);

    [Test]
    public void PostgreSql_ModelMatchesTheLatestSnapshot()
    {
        using var provider = BuildProvider(x => x.UsePostgreSql("Host=localhost;Database=unused"));
        using var context = CreateContext(provider);

        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Test]
    public void SqlServer_ModelMatchesTheLatestSnapshot()
    {
        using var provider = BuildProvider(x => x.UseSqlServer(SqlServerConnectionString));
        using var context = CreateContext(provider);

        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Test]
    public void PostgreSql_MigrationScript_CreatesEveryTableInTheBeaconSchema()
    {
        using var provider = BuildProvider(x => x.UsePostgreSql("Host=localhost;Database=unused"));
        using var context = CreateContext(provider);

        var script = context.GetService<IMigrator>().GenerateScript();

        var tables = PostgreSqlCreateTable.Matches(script).Select(x => x.Groups["name"].Value).ToList();
        tables.Should().NotBeEmpty();
        tables.Should().OnlyContain(x => x.StartsWith("beacon."));
        tables.Should().Contain("beacon.\"__EFMigrationsHistory\"");
        tables.Should().Contain("beacon.projects");
    }

    [Test]
    public void SqlServer_MigrationScript_CreatesTheBeaconSchemaAndEveryTableInIt()
    {
        using var provider = BuildProvider(x => x.UseSqlServer(SqlServerConnectionString));
        using var context = CreateContext(provider);

        var script = context.GetService<IMigrator>().GenerateScript();

        script.Should().Contain("CREATE SCHEMA [beacon]");
        var tables = SqlServerCreateTable.Matches(script).Select(x => x.Groups["name"].Value).ToList();
        tables.Should().NotBeEmpty();
        tables.Should().OnlyContain(x => x.StartsWith("[beacon]."));
        tables.Should().Contain("[beacon].[__EFMigrationsHistory]");
        tables.Should().Contain("[beacon].[Projects]");
    }

    [Test]
    public void PostgreSql_ContextModel_UsesTheBeaconSchema()
    {
        using var provider = BuildProvider(x => x.UsePostgreSql("Host=localhost;Database=unused"));
        using var context = CreateContext(provider);

        context.Model.GetDefaultSchema().Should().Be("beacon");
        context.Projects.ToQueryString().Should().Contain("FROM beacon.projects");
    }

    [Test]
    public void SqlServer_ContextModel_UsesTheBeaconSchema()
    {
        using var provider = BuildProvider(x => x.UseSqlServer(SqlServerConnectionString));
        using var context = CreateContext(provider);

        context.Model.GetDefaultSchema().Should().Be("beacon");
        context.Projects.ToQueryString().Should().Contain("FROM [beacon].[Projects]");
    }

    [Test]
    public void PostgreSqlMigrator_UsesTheModelAndScriptOfTheApplication()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        using var migrator = new PostgreSqlBeaconContext(BeaconPostgreSqlMigrator.ContextOptions(dataSource, setSearchPathOnOpen: true));
        using var provider = BuildProvider(x => x.UsePostgreSql("Host=localhost;Database=unused"));
        using var application = CreateContext(provider);

        migrator.Database.HasPendingModelChanges().Should().BeFalse();
        migrator.GetService<IMigrator>().GenerateScript().Should().Be(application.GetService<IMigrator>().GenerateScript());
    }

    [Test]
    public void SqlServerMigrator_UsesTheModelAndScriptOfTheApplication()
    {
        using var migrator = new SqlServerBeaconContext(BeaconSqlServerMigrator.ContextOptions(SqlServerConnectionString));
        using var provider = BuildProvider(x => x.UseSqlServer(SqlServerConnectionString));
        using var application = CreateContext(provider);

        migrator.Database.HasPendingModelChanges().Should().BeFalse();
        var script = migrator.GetService<IMigrator>().GenerateScript();
        script.Should().Contain("CREATE SCHEMA [beacon]");
        script.Should().Be(application.GetService<IMigrator>().GenerateScript());
    }

    // The schema overloads are obsolete: they still refuse any schema but beacon, and otherwise change nothing.
#pragma warning disable CS0618
    [Test]
    public void PostgreSql_ObsoleteSchemaOverload_WithBeacon_RegistersTheSameModel()
    {
        using var provider = BuildProvider(x => x.UsePostgreSql("Host=localhost;Database=unused", "beacon"));
        using var context = CreateContext(provider);

        context.Database.HasPendingModelChanges().Should().BeFalse();
        context.Projects.ToQueryString().Should().Contain("FROM beacon.projects");
    }

    [Test]
    public void SqlServer_ObsoleteSchemaOverload_WithBeacon_RegistersTheSameModel()
    {
        using var provider = BuildProvider(x => x.UseSqlServer(SqlServerConnectionString, "beacon"));
        using var context = CreateContext(provider);

        context.Database.HasPendingModelChanges().Should().BeFalse();
        context.Projects.ToQueryString().Should().Contain("FROM [beacon].[Projects]");
    }

    [Test]
    public void ObsoleteSchemaOverloads_RefuseASchemaOtherThanBeacon()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused");

        var actions = new Action[]
        {
            () => BuildProvider(x => x.UsePostgreSql("Host=localhost;Database=unused", "tenant_a")),
            () => BuildProvider(x => x.UsePostgreSql(dataSource, "tenant_a")),
            () => BuildProvider(x => x.UsePostgreSql(_ => dataSource, "tenant_a")),
            () => BuildProvider(x => x.UseSqlServer(SqlServerConnectionString, "tenant_a"))
        };

        foreach (var act in actions)
        {
            act.Should().Throw<InvalidOperationException>().WithMessage("*only supports*beacon*tenant_a*");
        }
    }

    [Test]
    public async Task ObsoleteMigratorSchemaOverloads_RefuseASchemaOtherThanBeacon_BeforeConnecting()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused");

        var actions = new Func<Task>[]
        {
            () => BeaconPostgreSqlMigrator.MigrateAsync("Host=localhost;Database=unused", "tenant_a"),
            () => BeaconPostgreSqlMigrator.MigrateAsync(dataSource, "tenant_a"),
            () => BeaconSqlServerMigrator.MigrateAsync(SqlServerConnectionString, "tenant_a")
        };

        foreach (var act in actions)
        {
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*only supports*beacon*tenant_a*");
        }
    }
#pragma warning restore CS0618

    [Test]
    public void UsePostgreSql_FromConfiguration_StillRefusesAnotherSchema()
    {
        var act = () => BuildProvider(x => x.UsePostgreSql(), schema: "tenant_a");

        act.Should().Throw<InvalidOperationException>().WithMessage("*only supports*beacon*tenant_a*");
    }

    [Test]
    public void UseSqlServer_FromConfiguration_StillRefusesAnotherSchema()
    {
        var act = () => BuildProvider(x => x.UseSqlServer(), schema: "tenant_a");

        act.Should().Throw<InvalidOperationException>().WithMessage("*only supports*beacon*tenant_a*");
    }

    private static BeaconContext CreateContext(ServiceProvider provider)
    {
        return provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
    }

    private static ServiceProvider BuildProvider(Action<BeaconBuilder> useDatabase, string? schema = null)
    {
        var services = new ServiceCollection();

        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = TestEncryptionKey.Value,
                ["ConnectionStrings:BeaconContext"] = "Host=localhost;Database=unused",
                [BeaconDatabaseConfiguration.SchemaKey] = schema
            })
            .Build();

        var builder = services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());
        useDatabase(builder);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
