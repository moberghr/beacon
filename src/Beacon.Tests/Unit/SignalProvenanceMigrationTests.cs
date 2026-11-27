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
using Beacon.Core.PostgreSql;
using Beacon.Core.SqlServer;
using Beacon.Core.Worker;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The migration that records which knowledge items an <c>ask</c> prompt carried and links a signal to the one it followed,
/// checked offline on both providers (nothing connects, nothing is migrated): it creates the signal context items table with
/// a cascading foreign key to the signals (so the retention job's bulk delete of signals removes their items) and indexes on
/// the signal and on (kind, item id), and adds the parent signal id (indexed, no foreign key), the edited-rerun flag (false
/// for every existing signal) and the feedback reason to the signals. It runs no SQL and backfills nothing. Up is exactly
/// the difference between the model before it and its target model, Down is exactly the way back, its target model is the
/// snapshot, the model has no pending changes, and it sorts last.
/// </summary>
[TestFixture]
public class SignalProvenanceMigrationTests
{
    private const string Suffix = "_AddSignalProvenance";

    [TestCase(false, "integer", "boolean")]
    [TestCase(true, "int", "bit")]
    public void TheMigration_AddsTheContextItemsAndTheSignalColumns_AndIsTheSnapshot(bool sqlServer, string intType, string boolType)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var signals = Named(sqlServer, "mcp_query_signals");
        var items = Named(sqlServer, "mcp_signal_context_items");

        var migration = MigrationNamed(context, migrations, Suffix);
        var previous = MigrationNamed(context, migrations, "_AddUserTokensValidAfter");
        var before = Initialized(previous.TargetModel!, initializer).GetRelationalModel();
        var target = Initialized(migration.TargetModel!, initializer).GetRelationalModel();

        migration.UpOperations.Should().NotContain(x => x is SqlOperation, "nothing rewrites or backfills a stored row");
        migration.DownOperations.Should().NotContain(x => x is SqlOperation);
        var table = migration.UpOperations.OfType<CreateTableOperation>().Should().ContainSingle().Subject;
        $"{table.Schema}.{table.Name}".Should().Be($"beacon.{items}");
        table.Columns.Select(x => x.Name)
            .Should().BeEquivalentTo(new[] { "id", "signal_id", "kind", "item_id", "rank", "created_time" }.Select(x => Named(sqlServer, x)));
        table.Columns.Where(x => x.Name != Named(sqlServer, "created_time"))
            .Should().OnlyContain(x => x.ColumnType == intType && !x.IsNullable, "the items hold ids, enums and ranks only");
        var foreignKey = table.ForeignKeys.Should().ContainSingle().Subject;
        foreignKey.Columns.Should().Equal(Named(sqlServer, "signal_id"));
        $"{foreignKey.PrincipalSchema}.{foreignKey.PrincipalTable}".Should().Be($"beacon.{signals}");
        foreignKey.PrincipalColumns.Should().Equal(Named(sqlServer, "id"));
        foreignKey.OnDelete.Should().Be(ReferentialAction.Cascade, "the retention job's bulk delete of signals removes their items");
        migration.UpOperations.Should().NotContain(x => x is AddForeignKeyOperation, "the parent signal id has no foreign key");

        var columns = migration.UpOperations.OfType<AddColumnOperation>().ToList();
        columns.Select(x => $"{x.Schema}.{x.Table}.{x.Name}")
            .Should().BeEquivalentTo(new[] { "parent_signal_id", "is_edited_rerun", "feedback_reason" }.Select(x => $"beacon.{signals}.{Named(sqlServer, x)}"));
        var parent = columns.Single(x => x.Name == Named(sqlServer, "parent_signal_id"));
        parent.ColumnType.Should().Be(intType);
        parent.IsNullable.Should().BeTrue();
        parent.DefaultValue.Should().BeNull();
        var reason = columns.Single(x => x.Name == Named(sqlServer, "feedback_reason"));
        reason.ColumnType.Should().Be(intType);
        reason.IsNullable.Should().BeTrue();
        reason.DefaultValue.Should().BeNull();
        var rerun = columns.Single(x => x.Name == Named(sqlServer, "is_edited_rerun"));
        rerun.ColumnType.Should().Be(boolType);
        rerun.IsNullable.Should().BeFalse();
        rerun.DefaultValue.Should().Be(false, "every existing signal is not an edited rerun");

        migration.UpOperations.OfType<CreateIndexOperation>().Select(x => $"{x.Table}({string.Join(",", x.Columns)}) {x.IsUnique}")
            .Should().BeEquivalentTo(
                $"{items}({Named(sqlServer, "signal_id")}) False",
                $"{items}({Named(sqlServer, "kind")},{Named(sqlServer, "item_id")}) False",
                $"{signals}({Named(sqlServer, "parent_signal_id")}) False");
        migration.UpOperations.Should().HaveCount(7);

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
    public void TheModel_CascadesTheContextItems_AndKeepsTheParentSignalIdWithoutAForeignKey(bool sqlServer)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();

        var signal = context.Model.FindEntityType(typeof(McpQuerySignal))!;
        signal.GetForeignKeys()
            .Should().NotContain(x => x.Properties.Any(y => y.Name == nameof(McpQuerySignal.ParentSignalId)), "a parent removed by retention leaves a harmless dangling id");
        signal.GetIndexes()
            .Should().Contain(x => x.Properties.Count == 1 && x.Properties[0].Name == nameof(McpQuerySignal.ParentSignalId));
        var foreignKey = context.Model.FindEntityType(typeof(McpSignalContextItem))!.GetForeignKeys().Should().ContainSingle().Subject;
        foreignKey.PrincipalEntityType.ClrType.Should().Be(typeof(McpQuerySignal));
        foreignKey.Properties.Select(x => x.Name).Should().Equal(nameof(McpSignalContextItem.SignalId));
        foreignKey.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        foreignKey.IsRequired.Should().BeTrue();
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
