using System.Globalization;
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
using Beacon.Core.Data.Enums;
using Beacon.Core.PostgreSql;
using Beacon.Core.SqlServer;
using Beacon.Core.Worker;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// The connection-change approval migration, checked offline on both providers (nothing connects, nothing is migrated): it
/// adds the data sources' approved connection hash (nullable, 100 characters: every existing source stays ungoverned), the
/// change requests' encrypted payload (nullable, unbounded), and a unique index on kind and resource that holds only
/// pending LLM connection requests, so a second one cannot be submitted alongside the first. It runs no SQL and backfills
/// nothing. Up is exactly the difference between the model before it and its target model, and Down is exactly the way
/// back. Its target model is the snapshot, the model has no pending changes, and it sorts last.
/// </summary>
[TestFixture]
public class ConnectionChangeApprovalMigrationTests
{
    private const string Suffix = "_AddConnectionChangeApproval";

    [TestCase(false)]
    [TestCase(true)]
    public void TheMigration_AddsTheTwoColumnsAndTheIndex_AndIsTheSnapshot(bool sqlServer)
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

        migration.UpOperations.Should().NotContain(x => x is SqlOperation, "nothing rewrites or backfills a stored row");
        migration.DownOperations.Should().NotContain(x => x is SqlOperation);
        migration.UpOperations.OfType<AddColumnOperation>().Select(x => $"{x.Schema}.{x.Table}.{x.Name}")
            .Should().BeEquivalentTo(
                $"beacon.{Named(sqlServer, "data_sources")}.{Named(sqlServer, "approved_target_hash")}",
                $"beacon.{Named(sqlServer, "change_approval_requests")}.{Named(sqlServer, "encrypted_payload")}");
        migration.UpOperations.OfType<CreateIndexOperation>().Should().ContainSingle();
        migration.UpOperations.Should().HaveCount(3);
        Signatures(migration.UpOperations)
            .Should().BeEquivalentTo(Signatures(differ.GetDifferences(before, target)), "Up is exactly the difference from the model before");
        Signatures(migration.DownOperations)
            .Should().BeEquivalentTo(Signatures(differ.GetDifferences(target, before)), "Down removes exactly what Up adds");
        // The next migration's own content is pinned by EmbeddingContentHashMigrationTests.
        var next = MigrationNamed(context, migrations, "_AddEmbeddingContentHash");
        Signatures(differ.GetDifferences(target, Initialized(next.TargetModel!, initializer).GetRelationalModel()))
            .Should().BeEquivalentTo(
                Signatures(next.UpOperations),
                "this migration's target model differs from the next one's only by the next migration");
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        migrations.Migrations.Keys
            .Order(StringComparer.Ordinal)
            .SkipWhile(x => !x.EndsWith("_AddRowSecurityModel", StringComparison.Ordinal))
            .Skip(1)
            .First()
            .Should().EndWith(Suffix, "it sorts after every earlier migration");
    }

    [TestCase(false, "character varying(100)", "text")]
    [TestCase(true, "nvarchar(100)", "nvarchar(max)")]
    public void TheColumns_AreNullable_WithoutADefault(bool sqlServer, string hashType, string payloadType)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();

        var columns = MigrationNamed(context, migrations, Suffix).UpOperations
            .OfType<AddColumnOperation>()
            .ToDictionary(x => x.Name);

        var hash = columns[Named(sqlServer, "approved_target_hash")];
        hash.ColumnType.Should().Be(hashType);
        hash.MaxLength.Should().Be(100);
        var payload = columns[Named(sqlServer, "encrypted_payload")];
        payload.ColumnType.Should().Be(payloadType);
        columns.Values.Should().OnlyContain(x => x.IsNullable, "every existing source stays ungoverned and every existing request holds no payload");
        columns.Values.Should().OnlyContain(x => x.DefaultValue == null && x.DefaultValueSql == null);
        columns.Values.Should().OnlyContain(x => x.ClrType == typeof(string));
    }

    [TestCase(false, "kind = 5 AND status = 1")]
    [TestCase(true, "[Kind] = 5 AND [Status] = 1")]
    public void TheIndex_HoldsOnePendingLlmConnectionRequest(bool sqlServer, string filter)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();

        var index = MigrationNamed(context, migrations, Suffix).UpOperations
            .OfType<CreateIndexOperation>()
            .Should().ContainSingle()
            .Subject;

        $"{index.Schema}.{index.Table}({string.Join(",", index.Columns)})"
            .Should().Be($"beacon.{Named(sqlServer, "change_approval_requests")}({Named(sqlServer, "kind")},{Named(sqlServer, "resource_id")})");
        index.IsUnique.Should().BeTrue();
        index.Filter.Should().Be(filter);
        filter.Should().Contain(((int)ChangeApprovalKind.LlmConnection).ToString(CultureInfo.InvariantCulture))
            .And.Contain(((int)ApprovalStatus.Pending).ToString(CultureInfo.InvariantCulture));
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
