using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
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
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The MCP audit correlation and maintenance heartbeat migration, checked offline on both providers: it adds the nullable
/// correlation id column to the MCP audit log and creates the heartbeat table with a unique job name, runs no SQL, its
/// target model is the snapshot, and it sorts last.
/// </summary>
[TestFixture]
public class McpAuditCorrelationHeartbeatMigrationTests
{
    private const string Suffix = "_AddMcpAuditCorrelationAndHeartbeats";

    [TestCase(false)]
    [TestCase(true)]
    public void TheMigration_AddsTheCorrelationColumnAndTheHeartbeatTable_AndDownRemovesThem(bool sqlServer)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var differ = context.GetService<IMigrationsModelDiffer>();

        var migration = MigrationNamed(context, migrations, Suffix);
        var previous = MigrationNamed(context, migrations, "_AddRowSecurityModel");
        var before = Initialized(previous.TargetModel!, initializer).GetRelationalModel();
        var target = Initialized(migration.TargetModel!, initializer).GetRelationalModel();

        migration.UpOperations.Should().NotContain(x => x is SqlOperation, "nothing rewrites a stored row");
        migration.DownOperations.Should().NotContain(x => x is SqlOperation);

        var column = migration.UpOperations.OfType<AddColumnOperation>().Should().ContainSingle().Subject;
        column.Table.Should().Be(sqlServer ? "McpAuditLogs" : "mcp_audit_logs");
        column.Name.Should().Be(sqlServer ? "CorrelationId" : "correlation_id");
        column.IsNullable.Should().BeTrue("rows written before it carry none");
        column.MaxLength.Should().Be(32);

        var table = migration.UpOperations.OfType<CreateTableOperation>().Should().ContainSingle().Subject;
        table.Name.Should().Be(sqlServer ? "MaintenanceHeartbeats" : "maintenance_heartbeats");
        table.Schema.Should().Be("beacon");
        migration.UpOperations.OfType<CreateIndexOperation>().Should().ContainSingle(x => x.IsUnique && x.Table == table.Name);

        migration.UpOperations.Select(x => x.GetType().Name + ":" + Describe(x))
            .Should().BeEquivalentTo(differ.GetDifferences(before, target).Select(x => x.GetType().Name + ":" + Describe(x)), "Up is exactly the difference from the model before");
        migration.DownOperations.Select(x => x.GetType().Name + ":" + Describe(x))
            .Should().BeEquivalentTo(differ.GetDifferences(target, before).Select(x => x.GetType().Name + ":" + Describe(x)), "Down removes exactly what Up adds");
        differ.GetDifferences(target, Initialized(migrations.ModelSnapshot!.Model, initializer).GetRelationalModel())
            .Should().BeEmpty("the migration's target model is the snapshot");
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        migrations.Migrations.Keys
            .Order(StringComparer.Ordinal)
            .Last()
            .Should().EndWith(Suffix, "it sorts after every earlier migration");
    }

    private static string Describe(MigrationOperation operation) => operation switch
    {
        AddColumnOperation x => $"{x.Table}.{x.Name}",
        DropColumnOperation x => $"{x.Table}.{x.Name}",
        CreateTableOperation x => x.Name,
        DropTableOperation x => x.Name,
        CreateIndexOperation x => $"{x.Table}.{x.Name}",
        DropIndexOperation x => $"{x.Table}.{x.Name}",
        _ => operation.GetType().Name
    };

    private static Migration MigrationNamed(BeaconContext context, IMigrationsAssembly migrations, string suffix)
    {
        var id = migrations.Migrations.Keys.Single(x => x.EndsWith(suffix, StringComparison.Ordinal));

        return migrations.CreateMigration(migrations.Migrations[id], context.Database.ProviderName!);
    }

    private static IModel Initialized(IModel model, IModelRuntimeInitializer initializer)
    {
        var finalized = model is IMutableModel mutable ? mutable.FinalizeModel() : model;

        return initializer.Initialize(finalized, designTime: true, validationLogger: null);
    }

    private static ServiceProvider BuildProvider(bool sqlServer)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:EncryptionKey"] = TestEncryptionKey.Value })
            .Build();
        var builder = services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());
        if (sqlServer)
        {
            builder.UseSqlServer("Server=localhost;Database=unused;Trusted_Connection=True;TrustServerCertificate=True");
        }
        else
        {
            builder.UsePostgreSql("Host=localhost;Database=unused;Username=unused;Password=unused");
        }

        return services.BuildServiceProvider();
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
