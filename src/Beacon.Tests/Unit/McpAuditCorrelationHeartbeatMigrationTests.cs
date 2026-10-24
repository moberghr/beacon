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
/// correlation id column to the MCP audit log and creates the heartbeat table with a unique job name, runs no SQL, and sorts
/// right before the MCP audit seal migration, which changes only what it adds.
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
        var previous = MigrationNamed(context, migrations, "_BackfillGoldenCaseEmbeddingProjects");
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
        // The next migration's own content is pinned by its own tests.
        var next = MigrationNamed(context, migrations, "_AddExecutionLogStatus");
        Signatures(differ.GetDifferences(target, Initialized(next.TargetModel!, initializer).GetRelationalModel()))
            .Should().BeEquivalentTo(
                Signatures(next.UpOperations),
                "this migration's target model differs from the next one's only by the next migration");
        migrations.Migrations.Keys
            .Order(StringComparer.Ordinal)
            .SkipWhile(x => !x.EndsWith(Suffix, StringComparison.Ordinal))
            .Skip(1)
            .First()
            .Should().EndWith("_AddExecutionLogStatus", "the next migration sorts right after it");
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

    private static List<string> Signatures(IEnumerable<MigrationOperation> operations)
    {
        var signatures = new List<string>();
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case CreateTableOperation table:
                    signatures.Add($"create {table.Schema}.{table.Name} pk {table.PrimaryKey?.Name}");
                    signatures.AddRange(table.Columns.Select(x => $"column {table.Name}.{x.Name} {Signature(x)}"));
                    signatures.AddRange(table.ForeignKeys.Select(Signature));
                    signatures.AddRange(table.UniqueConstraints.Select(x => $"unique {x.Name} {table.Name}({string.Join(",", x.Columns)})"));
                    break;
                case AddColumnOperation column:
                    signatures.Add($"add {column.Schema}.{column.Table}.{column.Name} {Signature(column)}");
                    break;
                case AddForeignKeyOperation foreignKey:
                    signatures.Add(Signature(foreignKey));
                    break;
                case CreateIndexOperation index:
                    signatures.Add($"index {index.Name} {index.Schema}.{index.Table}({string.Join(",", index.Columns)}) {index.IsUnique} {index.Filter}");
                    break;
                case DropTableOperation table:
                    signatures.Add($"drop {table.Schema}.{table.Name}");
                    break;
                case DropColumnOperation column:
                    signatures.Add($"drop {column.Schema}.{column.Table}.{column.Name}");
                    break;
                case DropForeignKeyOperation foreignKey:
                    signatures.Add($"drop fk {foreignKey.Name} {foreignKey.Schema}.{foreignKey.Table}");
                    break;
                default:
                    signatures.Add(operation.GetType().Name);
                    break;
            }
        }

        return signatures;
    }

    // Type, length, unicode, nullability, default, and the identity or value generation the provider records.
    private static string Signature(ColumnOperation column)
    {
        var generation = column.GetAnnotations()
            .Where(x => x.Name.Contains("ValueGeneration") || x.Name.Contains("Identity"))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => $"{x.Name}={x.Value}");

        return $"{column.ColumnType} len={column.MaxLength} unicode={column.IsUnicode} fixed={column.IsFixedLength} "
            + $"precision={column.Precision}/{column.Scale} null={column.IsNullable} default={column.DefaultValue} "
            + $"defaultSql={column.DefaultValueSql} computed={column.ComputedColumnSql} [{string.Join(";", generation)}]";
    }

    private static string Signature(AddForeignKeyOperation foreignKey)
    {
        return $"fk {foreignKey.Name} {foreignKey.Schema}.{foreignKey.Table}({string.Join(",", foreignKey.Columns)}) -> "
            + $"{foreignKey.PrincipalSchema}.{foreignKey.PrincipalTable}({string.Join(",", foreignKey.PrincipalColumns!)}) {foreignKey.OnDelete}";
    }

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
