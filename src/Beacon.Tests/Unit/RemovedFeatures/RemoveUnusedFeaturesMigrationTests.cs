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

namespace Beacon.Tests.Unit.RemovedFeatures;

/// <summary>
/// The migration that removes the dashboards, MCP session, AI alert and AI usage metrics tables and the audit log's session key, checked
/// offline on both providers (nothing connects, nothing is migrated): it runs no SQL, Up is exactly the difference between
/// the model before it and its target model, and Down is exactly the way back, so it can be reverted. Its target model is
/// the snapshot, the model has no pending changes, and it sorts last.
/// </summary>
[TestFixture]
public class RemoveUnusedFeaturesMigrationTests
{
    private const string Suffix = "_RemoveUnusedFeatures";

    [TestCase(false, "mcp_audit_logs.session_id", new[] { "ai_alert_configurations", "ai_conversation_histories", "ai_usage_metrics", "dashboard_permissions", "dashboard_widgets", "dashboards", "mcp_sessions" })]
    [TestCase(true, "McpAuditLogs.SessionId", new[] { "AiAlertConfigurations", "AiConversationHistories", "AiUsageMetrics", "DashboardPermissions", "DashboardWidgets", "Dashboards", "McpSessions" })]
    public void TheMigration_RemovesTheUnusedTables_AndTheSessionKey_AndDownRestoresThem(bool sqlServer, string sessionColumn, string[] tables)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var differ = context.GetService<IMigrationsModelDiffer>();

        var migration = MigrationNamed(context, migrations, Suffix);
        var previous = MigrationNamed(context, migrations, "_AddDataSourceDisable");
        var before = Initialized(previous.TargetModel!, initializer).GetRelationalModel();
        var target = Initialized(migration.TargetModel!, initializer).GetRelationalModel();

        migration.UpOperations.Should().NotContain(x => x is SqlOperation, "the migration removes structure, it rewrites no row");
        migration.DownOperations.Should().NotContain(x => x is SqlOperation);
        migration.UpOperations.OfType<DropTableOperation>().Select(x => x.Name).Should().BeEquivalentTo(tables);
        migration.UpOperations.OfType<DropColumnOperation>().Select(x => $"{x.Table}.{x.Name}").Should().Equal(sessionColumn);
        migration.UpOperations.OfType<DropColumnOperation>().Should().OnlyContain(x => x.Schema == "beacon");
        migration.UpOperations.OfType<DropTableOperation>().Should().OnlyContain(x => x.Schema == "beacon");
        Signatures(migration.UpOperations)
            .Should().BeEquivalentTo(Signatures(differ.GetDifferences(before, target)), "Up is exactly the difference from the model before");
        Signatures(migration.DownOperations)
            .Should().BeEquivalentTo(Signatures(differ.GetDifferences(target, before)), "Down restores exactly what Up removes");
        AssertUpOrder(migration.UpOperations.ToList(), sqlServer);
        AssertDownOrder(migration.DownOperations.ToList(), sqlServer);
        differ.GetDifferences(target, Initialized(migrations.ModelSnapshot!.Model, initializer).GetRelationalModel())
            .Should().BeEmpty("the migration's target model is the snapshot");
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        migrations.Migrations.Keys
            .Order(StringComparer.Ordinal)
            .Last()
            .Should().EndWith(Suffix, "it sorts after every earlier migration");
    }

    // Dependents go first: the key and the index on the audit log before its column and before the table they point at, and
    // every child table before its parent.
    private static void AssertUpOrder(List<MigrationOperation> operations, bool sqlServer)
    {
        var dropForeignKey = operations.FindIndex(x => x is DropForeignKeyOperation);
        var dropIndex = operations.FindIndex(x => x is DropIndexOperation);
        var dropColumn = operations.FindIndex(x => x is DropColumnOperation);
        var sessions = DropTableIndex(operations, sqlServer ? "McpSessions" : "mcp_sessions");

        dropForeignKey.Should().BeGreaterThanOrEqualTo(0);
        dropIndex.Should().BeGreaterThanOrEqualTo(0);
        dropForeignKey.Should().BeLessThan(dropColumn, "the key goes before the column it covers");
        dropIndex.Should().BeLessThan(dropColumn, "the index goes before the column it covers");
        dropForeignKey.Should().BeLessThan(sessions, "the key goes before the table it points at");
        dropIndex.Should().BeLessThan(sessions);
        dropColumn.Should().BeLessThan(sessions, "the column goes before the table it referenced");
        DropTableIndex(operations, sqlServer ? "AiConversationHistories" : "ai_conversation_histories")
            .Should().BeLessThan(DropTableIndex(operations, sqlServer ? "AiAlertConfigurations" : "ai_alert_configurations"));
        var dashboards = DropTableIndex(operations, sqlServer ? "Dashboards" : "dashboards");
        DropTableIndex(operations, sqlServer ? "DashboardPermissions" : "dashboard_permissions").Should().BeLessThan(dashboards);
        DropTableIndex(operations, sqlServer ? "DashboardWidgets" : "dashboard_widgets").Should().BeLessThan(dashboards);
    }

    // Parents first: every table before the tables that point at it, and the audit log's column and session table before
    // the key between them.
    private static void AssertDownOrder(List<MigrationOperation> operations, bool sqlServer)
    {
        var sessions = CreateTableIndex(operations, sqlServer ? "McpSessions" : "mcp_sessions");
        var foreignKey = operations.FindIndex(x => x is AddForeignKeyOperation key && key.PrincipalTable == (sqlServer ? "McpSessions" : "mcp_sessions"));
        var column = operations.FindIndex(x => x is AddColumnOperation added && added.Name == (sqlServer ? "SessionId" : "session_id"));

        foreignKey.Should().BeGreaterThanOrEqualTo(0);
        column.Should().BeGreaterThanOrEqualTo(0);
        sessions.Should().BeLessThan(foreignKey, "the session table exists before the key to it");
        column.Should().BeLessThan(foreignKey, "the column exists before the key over it");
        CreateTableIndex(operations, sqlServer ? "AiAlertConfigurations" : "ai_alert_configurations")
            .Should().BeLessThan(CreateTableIndex(operations, sqlServer ? "AiConversationHistories" : "ai_conversation_histories"));
        var dashboards = CreateTableIndex(operations, sqlServer ? "Dashboards" : "dashboards");
        dashboards.Should().BeLessThan(CreateTableIndex(operations, sqlServer ? "DashboardPermissions" : "dashboard_permissions"));
        dashboards.Should().BeLessThan(CreateTableIndex(operations, sqlServer ? "DashboardWidgets" : "dashboard_widgets"));
    }

    private static int DropTableIndex(List<MigrationOperation> operations, string name)
    {
        var index = operations.FindIndex(x => x is DropTableOperation table && table.Name == name);
        index.Should().BeGreaterThanOrEqualTo(0, $"{name} is dropped");

        return index;
    }

    private static int CreateTableIndex(List<MigrationOperation> operations, string name)
    {
        var index = operations.FindIndex(x => x is CreateTableOperation table && table.Name == name);
        index.Should().BeGreaterThanOrEqualTo(0, $"{name} is created");

        return index;
    }

    // What an operation changes, in a form two operation lists can be compared by: names, tables, columns with their type,
    // length, default and value generation, unique keys, foreign keys with their delete rule, and indexes with their uniqueness.
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
                    signatures.Add($"index {index.Name} {index.Schema}.{index.Table}({string.Join(",", index.Columns)}) {index.IsUnique}");
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
                case DropIndexOperation index:
                    signatures.Add($"drop index {index.Name} {index.Schema}.{index.Table}");
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

    // A model built by a migration or a snapshot, made ready for the differ as the migrator makes it.
    private static IModel Initialized(IModel model, IModelRuntimeInitializer initializer)
    {
        var finalized = model is IMutableModel mutable ? mutable.FinalizeModel() : model;

        return initializer.Initialize(finalized, designTime: true, validationLogger: null);
    }

    private static ServiceProvider BuildProvider(bool sqlServer)
    {
        var services = new ServiceCollection();
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
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
