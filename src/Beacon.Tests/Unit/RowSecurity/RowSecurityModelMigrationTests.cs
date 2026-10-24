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

namespace Beacon.Tests.Unit.RowSecurity;

/// <summary>
/// The row-security model migration, checked offline on both providers (nothing connects, nothing is migrated): it creates
/// the attribute, user value, policy, policy version and table class tables and adds the data sources' row-security
/// version (0 on every existing source), and runs no SQL. Up is exactly the difference between the model before it and its
/// target model, and Down is exactly the way back, each in dependency order. An attribute key is unique among live
/// attributes only, a user holds a value once, a table has one class per source and a policy one version per number. Enums
/// are stored as integers; table names and values compare ordinally (SQL Server through a binary collation). A data source
/// cannot be deleted under its policies or classes, a user's or an attribute's values go with it, a policy's versions go
/// with the policy, and the active version adds no second cascade path. Its target model differs from the next migration's
/// only by that migration, the model has no pending changes, and it sorts right after the unused-feature removal.
/// </summary>
[TestFixture]
public class RowSecurityModelMigrationTests
{
    private const string Suffix = "_AddRowSecurityModel";

    private static readonly string[] Tables =
    [
        "attribute_definitions",
        "row_policies",
        "row_policy_versions",
        "table_classifications",
        "user_attribute_values"
    ];

    [TestCase(false)]
    [TestCase(true)]
    public void TheMigration_CreatesTheRowSecurityTables_AndTheSourceVersion_AndDownRemovesThem(bool sqlServer)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var differ = context.GetService<IMigrationsModelDiffer>();

        var migration = MigrationNamed(context, migrations, Suffix);
        var previous = MigrationNamed(context, migrations, "_RemoveUnusedFeatures");
        var before = Initialized(previous.TargetModel!, initializer).GetRelationalModel();
        var target = Initialized(migration.TargetModel!, initializer).GetRelationalModel();

        migration.UpOperations.Should().NotContain(x => x is SqlOperation, "nothing rewrites or backfills a stored row");
        migration.DownOperations.Should().NotContain(x => x is SqlOperation);
        migration.UpOperations.OfType<CreateTableOperation>().Select(x => x.Name)
            .Should().BeEquivalentTo(Tables.Select(x => Named(sqlServer, x)));
        migration.UpOperations.OfType<CreateTableOperation>().Should().OnlyContain(x => x.Schema == "beacon");
        var version = migration.UpOperations.OfType<AddColumnOperation>().Should().ContainSingle().Subject;
        $"{version.Schema}.{version.Table}.{version.Name}"
            .Should().Be($"beacon.{Named(sqlServer, "data_sources")}.{Named(sqlServer, "row_security_version")}");
        version.ClrType.Should().Be<long>();
        version.IsNullable.Should().BeFalse();
        version.DefaultValue.Should().Be(0L, "every existing source starts at version 0");
        migration.DownOperations.OfType<DropTableOperation>().Select(x => x.Name)
            .Should().BeEquivalentTo(Tables.Select(x => Named(sqlServer, x)));
        Signatures(migration.UpOperations)
            .Should().BeEquivalentTo(Signatures(differ.GetDifferences(before, target)), "Up is exactly the difference from the model before");
        Signatures(migration.DownOperations)
            .Should().BeEquivalentTo(Signatures(differ.GetDifferences(target, before)), "Down removes exactly what Up adds");
        // The next migration's own content is pinned by ConnectionChangeApprovalMigrationTests.
        var next = MigrationNamed(context, migrations, "_AddConnectionChangeApproval");
        Signatures(differ.GetDifferences(target, Initialized(next.TargetModel!, initializer).GetRelationalModel()))
            .Should().BeEquivalentTo(
                Signatures(next.UpOperations),
                "this migration's target model differs from the next one's only by the next migration");
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        migrations.Migrations.Keys
            .Order(StringComparer.Ordinal)
            .SkipWhile(x => !x.EndsWith("_RemoveUnusedFeatures", StringComparison.Ordinal))
            .Skip(1)
            .First()
            .Should().EndWith(Suffix, "it sorts after every earlier migration");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TheMigration_CreatesAndDropsInDependencyOrder(bool sqlServer)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var migration = MigrationNamed(context, migrations, Suffix);
        var up = migration.UpOperations.ToList();
        var down = migration.DownOperations.ToList();

        var activeVersionKey = up.FindIndex(x => x is AddForeignKeyOperation key && key.Table == Named(sqlServer, "row_policies"));

        TableIndex<CreateTableOperation>(up, sqlServer, "row_policies")
            .Should().BeLessThan(TableIndex<CreateTableOperation>(up, sqlServer, "row_policy_versions"), "a version's key points at its policy");
        TableIndex<CreateTableOperation>(up, sqlServer, "attribute_definitions")
            .Should().BeLessThan(TableIndex<CreateTableOperation>(up, sqlServer, "user_attribute_values"), "a value's key points at its attribute");
        activeVersionKey.Should().BeGreaterThan(TableIndex<CreateTableOperation>(up, sqlServer, "row_policy_versions"), "the active version key needs both tables");
        down.FindIndex(x => x is DropForeignKeyOperation key && key.Table == Named(sqlServer, "row_policies"))
            .Should().Be(0, "the active version key goes first: the policies and their versions point at each other");
        TableIndex<DropTableOperation>(down, sqlServer, "row_policy_versions")
            .Should().BeLessThan(TableIndex<DropTableOperation>(down, sqlServer, "row_policies"), "the versions go before their policies");
        TableIndex<DropTableOperation>(down, sqlServer, "user_attribute_values")
            .Should().BeLessThan(TableIndex<DropTableOperation>(down, sqlServer, "attribute_definitions"), "the values go before their attributes");
    }

    [TestCase(false, "archived_time IS NULL")]
    [TestCase(true, "[ArchivedTime] IS NULL")]
    public void TheIndexes_HoldAKeyAmongLiveAttributes_AValueOnce_AClassPerTable_AndAVersionPerNumber(bool sqlServer, string liveFilter)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();

        var indexes = MigrationNamed(context, migrations, Suffix).UpOperations
            .OfType<CreateIndexOperation>()
            .Select(x => $"{x.Table}({string.Join(",", x.Columns)}) unique={x.IsUnique} {x.Filter}".TrimEnd())
            .ToList();

        indexes.Should().BeEquivalentTo(
        [
            $"{Named(sqlServer, "attribute_definitions")}({Named(sqlServer, "key")}) unique=True {liveFilter}",
            $"{Named(sqlServer, "user_attribute_values")}({Named(sqlServer, "user_id")},{Named(sqlServer, "attribute_definition_id")},{Named(sqlServer, "value")}) unique=True",
            $"{Named(sqlServer, "user_attribute_values")}({Named(sqlServer, "attribute_definition_id")}) unique=False",
            $"{Named(sqlServer, "table_classifications")}({Named(sqlServer, "data_source_id")},{Named(sqlServer, "schema_name")},{Named(sqlServer, "table_name")}) unique=True",
            $"{Named(sqlServer, "row_policies")}({Named(sqlServer, "data_source_id")},{Named(sqlServer, "schema_name")},{Named(sqlServer, "table_name")}) unique=False",
            $"{Named(sqlServer, "row_policies")}({Named(sqlServer, "active_version_id")}) unique=False",
            $"{Named(sqlServer, "row_policy_versions")}({Named(sqlServer, "row_policy_id")},{Named(sqlServer, "version_number")}) unique=True",
            $"{Named(sqlServer, "row_policy_versions")}({Named(sqlServer, "row_policy_id")},{Named(sqlServer, "status")}) unique=False"
        ]);
    }

    [TestCase(false, "integer", null)]
    [TestCase(true, "int", "Latin1_General_100_BIN2")]
    public void TheColumns_StoreEnumsAsIntegers_AndTableNamesAndValuesOrdinally(bool sqlServer, string integerType, string? ordinalCollation)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        string[] enums = ["attribute_definitions.value_type", "row_policy_versions.status", "table_classifications.class", "table_classifications.requested_class"];
        string[] ordinal = ["user_attribute_values.value", "row_policies.schema_name", "row_policies.table_name", "table_classifications.schema_name", "table_classifications.table_name"];

        var columns = MigrationNamed(context, migrations, Suffix).UpOperations
            .OfType<CreateTableOperation>()
            .SelectMany(x => x.Columns)
            .ToDictionary(x => $"{x.Table}.{x.Name}");

        enums.Select(x => columns[Column(sqlServer, x)].ColumnType).Should().AllBe(integerType);
        ordinal.Select(x => columns[Column(sqlServer, x)].Collation).Should().AllBe(ordinalCollation);
        columns
            .Where(x => !ordinal.Select(y => Column(sqlServer, y)).Contains(x.Key))
            .Should().OnlyContain(x => x.Value.Collation == null, "only table names and values get a collation of their own");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TheForeignKeys_KeepASourceUnderItsPolicies_AndTakeValuesAndVersionsWithTheirOwner(bool sqlServer)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var operations = MigrationNamed(context, migrations, Suffix).UpOperations;

        var keys = operations
            .OfType<CreateTableOperation>()
            .SelectMany(x => x.ForeignKeys)
            .Concat(operations.OfType<AddForeignKeyOperation>())
            .Select(x => $"{x.Table}({string.Join(",", x.Columns)}) -> {x.PrincipalTable} {x.OnDelete}")
            .ToList();

        keys.Should().BeEquivalentTo(
        [
            $"{Named(sqlServer, "table_classifications")}({Named(sqlServer, "data_source_id")}) -> {Named(sqlServer, "data_sources")} {ReferentialAction.Restrict}",
            $"{Named(sqlServer, "row_policies")}({Named(sqlServer, "data_source_id")}) -> {Named(sqlServer, "data_sources")} {ReferentialAction.Restrict}",
            $"{Named(sqlServer, "user_attribute_values")}({Named(sqlServer, "user_id")}) -> {Named(sqlServer, "users")} {ReferentialAction.Cascade}",
            $"{Named(sqlServer, "user_attribute_values")}({Named(sqlServer, "attribute_definition_id")}) -> {Named(sqlServer, "attribute_definitions")} {ReferentialAction.Cascade}",
            $"{Named(sqlServer, "row_policy_versions")}({Named(sqlServer, "row_policy_id")}) -> {Named(sqlServer, "row_policies")} {ReferentialAction.Cascade}",
            $"{Named(sqlServer, "row_policies")}({Named(sqlServer, "active_version_id")}) -> {Named(sqlServer, "row_policy_versions")} {ReferentialAction.NoAction}"
        ]);
    }

    private static int TableIndex<TOperation>(List<MigrationOperation> operations, bool sqlServer, string table)
        where TOperation : MigrationOperation
    {
        var index = operations.FindIndex(x => x is TOperation and ITableMigrationOperation operation && operation.Table == Named(sqlServer, table));
        index.Should().BeGreaterThanOrEqualTo(0, $"{table} is in the migration");

        return index;
    }

    // "table.column", in the provider's naming.
    private static string Column(bool sqlServer, string column)
    {
        var dot = column.IndexOf('.', StringComparison.Ordinal);

        return $"{Named(sqlServer, column[..dot])}.{Named(sqlServer, column[(dot + 1)..])}";
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
