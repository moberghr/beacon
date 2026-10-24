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

namespace Beacon.Tests.Unit.DataQuality;

/// <summary>
/// The contract rule history migration, checked offline on both providers (nothing connects, nothing is migrated): it adds
/// one nullable archive timestamp to the contract rules (null, live, on every existing rule) and runs no SQL; Up is exactly
/// the difference between the model before it and its target model, Down is the way back, its target model is the
/// snapshot, the model has no pending changes, and it sorts last.
/// </summary>
[TestFixture]
public class ContractRuleHistoryMigrationTests
{
    private const string Suffix = "_AddContractRuleHistory";

    [TestCase(false, "data_contract_rules", "archived_time", "timestamp with time zone")]
    [TestCase(true, "DataContractRules", "ArchivedTime", "datetime2")]
    public void TheMigration_AddsOneNullableArchiveTimestampToTheRules_AndDownRemovesIt(bool sqlServer, string table, string column, string type)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var differ = context.GetService<IMigrationsModelDiffer>();

        var migration = MigrationNamed(context, migrations, Suffix);
        var previous = MigrationNamed(context, migrations, "_AddExecutionLogStatus");
        var before = Initialized(previous.TargetModel!, initializer).GetRelationalModel();
        var target = Initialized(migration.TargetModel!, initializer).GetRelationalModel();

        migration.UpOperations.Should().ContainSingle()
            .Which.Should().BeOfType<AddColumnOperation>()
            .Which.Should().BeEquivalentTo(
                new { Schema = "beacon", Table = table, Name = column, ColumnType = type, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null },
                x => x.ExcludingMissingMembers());
        migration.DownOperations.Should().ContainSingle()
            .Which.Should().BeOfType<DropColumnOperation>()
            .Which.Should().BeEquivalentTo(new { Schema = "beacon", Table = table, Name = column }, x => x.ExcludingMissingMembers());
        differ.GetDifferences(before, target)
            .Select(Signature)
            .Should().Equal(migration.UpOperations.Select(Signature), "Up is exactly the difference from the model before");
        differ.GetDifferences(target, before)
            .Select(Signature)
            .Should().Equal(migration.DownOperations.Select(Signature), "Down removes exactly what Up adds");
        // The next migration's own content is pinned by its own tests.
        var next = MigrationNamed(context, migrations, "_AddDocumentationSectionEdits");
        Signatures(differ.GetDifferences(target, Initialized(next.TargetModel!, initializer).GetRelationalModel()))
            .Should().BeEquivalentTo(
                Signatures(next.UpOperations),
                "this migration's target model differs from the next one's only by the next migration");
        migrations.Migrations.Keys
            .Order(StringComparer.Ordinal)
            .SkipWhile(x => !x.EndsWith(Suffix, StringComparison.Ordinal))
            .Skip(1)
            .First()
            .Should().EndWith("_AddDocumentationSectionEdits", "the next migration sorts right after it");
    }

    private static string Signature(MigrationOperation operation)
    {
        return operation switch
        {
            AddColumnOperation x => $"add {x.Schema}.{x.Table}.{x.Name} {x.ColumnType} null={x.IsNullable} default={x.DefaultValue}",
            DropColumnOperation x => $"drop {x.Schema}.{x.Table}.{x.Name}",
            _ => operation.GetType().Name
        };
    }

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
