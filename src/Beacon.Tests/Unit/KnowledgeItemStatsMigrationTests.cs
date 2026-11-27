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
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.PostgreSql;
using Beacon.Core.SqlServer;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using McpSettingsEntity = Beacon.Core.Data.Entities.McpSettings;

namespace Beacon.Tests.Unit;

/// <summary>
/// The migration behind knowledge learning, checked offline on both providers (nothing connects, nothing is migrated): it
/// creates the knowledge item stats table (ids, counts, scores and times only, no foreign key, unique on project, kind and
/// item), adds the two settings that ship off (false on the global row, inherit on a project's row), the pattern's last
/// replay outcome (null: never replayed), the eval run's trigger (every existing run was started by a person: Manual) and the
/// patch's source pattern id (null, no foreign key). It runs no SQL and backfills nothing. Up is exactly the difference
/// between the model before it and its target model, Down is exactly the way back, its target model is the snapshot, the
/// model has no pending changes, and it sorts last.
/// </summary>
[TestFixture]
public class KnowledgeItemStatsMigrationTests
{
    private const string Suffix = "_AddKnowledgeItemStats";

    [TestCase(false, "integer", "boolean", "double precision", "timestamp with time zone")]
    [TestCase(true, "int", "bit", "float", "datetime2")]
    public void TheMigration_CreatesTheStatsTable_AndAddsTheNewColumns_AndIsTheSnapshot(
        bool sqlServer, string intType, string boolType, string doubleType, string timeType)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var stats = Named(sqlServer, "mcp_knowledge_item_stats");

        var migration = MigrationNamed(context, migrations, Suffix);
        var previous = MigrationNamed(context, migrations, "_AddSignalProvenance");
        var before = Initialized(previous.TargetModel!, initializer).GetRelationalModel();
        var target = Initialized(migration.TargetModel!, initializer).GetRelationalModel();

        migration.UpOperations.Should().NotContain(x => x is SqlOperation, "nothing rewrites or backfills a stored row");
        migration.DownOperations.Should().NotContain(x => x is SqlOperation);
        var table = migration.UpOperations.OfType<CreateTableOperation>().Should().ContainSingle().Subject;
        $"{table.Schema}.{table.Name}".Should().Be($"beacon.{stats}");
        table.Columns.Select(x => $"{x.Name} {x.ColumnType} {x.IsNullable}")
            .Should().BeEquivalentTo(
                [
                    $"{Named(sqlServer, "id")} {intType} False",
                    $"{Named(sqlServer, "project_id")} {intType} False",
                    $"{Named(sqlServer, "kind")} {intType} False",
                    $"{Named(sqlServer, "item_id")} {intType} False",
                    $"{Named(sqlServer, "helpful_score")} {doubleType} False",
                    $"{Named(sqlServer, "harmful_score")} {doubleType} False",
                    $"{Named(sqlServer, "signal_count")} {intType} False",
                    $"{Named(sqlServer, "caller_count")} {intType} False",
                    $"{Named(sqlServer, "computed_at")} {timeType} False",
                    $"{Named(sqlServer, "created_time")} {timeType} False"
                ],
                "the stats hold ids, enums, counts, scores and times only");
        table.ForeignKeys.Should().BeEmpty("the item id points at a different table by kind");
        migration.UpOperations.Should().NotContain(x => x is AddForeignKeyOperation, "neither the stats nor the source pattern id has a foreign key");

        var columns = migration.UpOperations.OfType<AddColumnOperation>().ToList();
        columns.Select(x => $"{x.Schema}.{x.Table}.{x.Name} {x.ColumnType} {x.IsNullable} {x.DefaultValue ?? "none"}")
            .Should().BeEquivalentTo(
                [
                    $"beacon.{Named(sqlServer, "mcp_settings")}.{Named(sqlServer, "enable_knowledge_item_boost")} {boolType} False False",
                    $"beacon.{Named(sqlServer, "mcp_settings")}.{Named(sqlServer, "enable_scheduled_eval")} {boolType} False False",
                    $"beacon.{Named(sqlServer, "mcp_project_settings")}.{Named(sqlServer, "enable_knowledge_item_boost")} {boolType} True none",
                    $"beacon.{Named(sqlServer, "mcp_project_settings")}.{Named(sqlServer, "enable_scheduled_eval")} {boolType} True none",
                    $"beacon.{Named(sqlServer, "mcp_learned_patterns")}.{Named(sqlServer, "last_replay_passed")} {boolType} True none",
                    $"beacon.{Named(sqlServer, "mcp_eval_runs")}.{Named(sqlServer, "trigger")} {intType} False {(int)McpEvalRunTrigger.Manual}",
                    $"beacon.{Named(sqlServer, "mcp_documentation_patches")}.{Named(sqlServer, "source_pattern_id")} {intType} True none"
                ],
                "both settings ship off, a project inherits them, and no existing pattern, run or patch gets a made-up value");
        columns.Single(x => x.Table == Named(sqlServer, "mcp_eval_runs")).DefaultValue
            .Should().Be((int)McpEvalRunTrigger.Manual, "every existing eval run was started by a person");

        migration.UpOperations.OfType<CreateIndexOperation>().Select(x => $"{x.Table}({string.Join(",", x.Columns)}) {x.IsUnique}")
            .Should().Equal($"{stats}({Named(sqlServer, "project_id")},{Named(sqlServer, "kind")},{Named(sqlServer, "item_id")}) True");
        migration.UpOperations.Should().HaveCount(9);

        Signatures(migration.UpOperations)
            .Should().BeEquivalentTo(Signatures(differ.GetDifferences(before, target)), "Up is exactly the difference from the model before");
        Signatures(migration.DownOperations)
            .Should().BeEquivalentTo(Signatures(differ.GetDifferences(target, before)), "Down removes exactly what Up adds");
        differ.GetDifferences(target, Initialized(migrations.ModelSnapshot!.Model, initializer).GetRelationalModel())
            .Should().BeEmpty("the migration's target model is the snapshot");
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        migrations.Migrations.Keys
            .Order(StringComparer.Ordinal)
            .Last()
            .Should().EndWith(Suffix, "it sorts after every earlier migration");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TheModel_KeepsTheStatsUniquePerItem_WithoutStringsOrForeignKeys_AndTheSourcePatternIdWithoutAForeignKey(bool sqlServer)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();

        var stat = context.Model.FindEntityType(typeof(McpKnowledgeItemStat))!;
        stat.GetProperties().Should().NotContain(x => x.ClrType == typeof(string), "the stats carry no content (§1.16)");
        typeof(McpKnowledgeItemStat).GetProperties().Should().NotContain(x => x.PropertyType == typeof(string));
        stat.GetForeignKeys().Should().BeEmpty();
        stat.GetNavigations().Should().BeEmpty();
        var unique = stat.GetIndexes().Should().ContainSingle().Subject;
        unique.IsUnique.Should().BeTrue("the recompute keeps one row per project and item");
        unique.Properties.Select(x => x.Name)
            .Should().Equal(nameof(McpKnowledgeItemStat.ProjectId), nameof(McpKnowledgeItemStat.Kind), nameof(McpKnowledgeItemStat.ItemId));
        context.Model.FindEntityType(typeof(McpDocumentationPatch))!.GetForeignKeys()
            .Should().NotContain(x => x.Properties.Any(y => y.Name == nameof(McpDocumentationPatch.SourcePatternId)), "a pattern removed later leaves a harmless dangling id");

        var settings = context.Model.FindEntityType(typeof(McpSettingsEntity))!;
        settings.FindProperty(nameof(McpSettingsEntity.EnableKnowledgeItemBoost))!.GetDefaultValue().Should().Be(false);
        settings.FindProperty(nameof(McpSettingsEntity.EnableScheduledEval))!.GetDefaultValue().Should().Be(false);
        new McpEvalRun().Trigger.Should().Be(McpEvalRunTrigger.Manual);
    }

    // PostgreSQL names tables and columns in snake_case; SQL Server keeps the model's PascalCase.
    private static string Named(bool sqlServer, string name)
    {
        return sqlServer
            ? string.Concat(name.Split('_').Select(x => char.ToUpperInvariant(x[0]) + x[1..]))
            : name;
    }

    // What an operation changes, in a form two operation lists can be compared by: names, tables, columns with their type,
    // length, default and value generation, foreign keys with their delete rule, and indexes with their uniqueness and filter.
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
                case DropIndexOperation index:
                    signatures.Add($"drop index {index.Name} {index.Schema}.{index.Table}");
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
