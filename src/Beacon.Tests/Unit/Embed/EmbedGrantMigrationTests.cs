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

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// The embed-grant migration, checked offline on both providers (nothing connects, nothing is migrated): it creates the
/// one <c>embed_grants</c> table (the grant row is also the revocation record), with a unique public id and a unique
/// secret hash (fixed-length, non-Unicode), the project/revocation index and restricting foreign keys to the project, the
/// query and the pinned version. It runs no SQL and backfills nothing. Up is exactly the difference between the model
/// before it and its target model, and Down is exactly the way back. Its target model is the snapshot, the model has no
/// pending changes, and it sorts last.
/// </summary>
[TestFixture]
public class EmbedGrantMigrationTests
{
    private const string Suffix = "_AddEmbedGrants";

    [TestCase(false)]
    [TestCase(true)]
    public void TheMigration_CreatesTheGrantTable_AndIsTheSnapshot(bool sqlServer)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var differ = context.GetService<IMigrationsModelDiffer>();

        var migration = MigrationNamed(context, migrations, Suffix);
        var previous = MigrationNamed(context, migrations, "_AddBackgroundTasks");
        var before = Initialized(previous.TargetModel!, initializer).GetRelationalModel();
        var target = Initialized(migration.TargetModel!, initializer).GetRelationalModel();

        migration.UpOperations.Should().NotContain(x => x is SqlOperation, "nothing rewrites or backfills a stored row");
        migration.DownOperations.Should().NotContain(x => x is SqlOperation);
        var table = migration.UpOperations.OfType<CreateTableOperation>().Should().ContainSingle().Subject;
        $"{table.Schema}.{table.Name}".Should().Be($"beacon.{Named(sqlServer, "embed_grants")}");
        table.ForeignKeys.Select(x => $"{x.PrincipalTable}:{x.OnDelete}")
            .Should().BeEquivalentTo(
                $"{Named(sqlServer, "projects")}:{ReferentialAction.Restrict}",
                $"{Named(sqlServer, "queries")}:{ReferentialAction.Restrict}",
                $"{Named(sqlServer, "query_versions")}:{ReferentialAction.Restrict}");
        migration.UpOperations.OfType<CreateIndexOperation>()
            .Select(x => $"{string.Join(",", x.Columns)}:{x.IsUnique}")
            .Should().BeEquivalentTo(
                $"{Named(sqlServer, "public_id")}:True",
                $"{Named(sqlServer, "secret_hash")}:True",
                $"{Named(sqlServer, "project_id")},{Named(sqlServer, "revoked_time")}:False",
                $"{Named(sqlServer, "query_id")}:False",
                $"{Named(sqlServer, "query_version_id")}:False");
        migration.DownOperations.OfType<DropTableOperation>().Should().ContainSingle()
            .Which.Name.Should().Be(Named(sqlServer, "embed_grants"));
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

    [TestCase(false, "character(32)", "character(64)", "text")]
    [TestCase(true, "char(32)", "char(64)", "nvarchar(max)")]
    public void TheColumns_HoldOnlyHashesAndIds_AndTheRevocationIsNullable(bool sqlServer, string publicIdType, string hashType, string jsonType)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();

        var columns = MigrationNamed(context, migrations, Suffix).UpOperations
            .OfType<CreateTableOperation>()
            .Single()
            .Columns
            .ToDictionary(x => x.Name);

        var publicId = columns[Named(sqlServer, "public_id")];
        publicId.ColumnType.Should().Be(publicIdType);
        publicId.IsFixedLength.Should().BeTrue();
        publicId.IsUnicode.Should().BeFalse();
        var hash = columns[Named(sqlServer, "secret_hash")];
        hash.ColumnType.Should().Be(hashType);
        hash.IsFixedLength.Should().BeTrue();
        columns.Keys.Should().NotContain(x => x.Contains("secret", StringComparison.OrdinalIgnoreCase) && x != Named(sqlServer, "secret_hash"), "the secret itself is never stored");
        foreach (var json in new[] { "allowed_origins_json", "fixed_parameters_json", "locked_parameter_names_json" })
        {
            columns[Named(sqlServer, json)].ColumnType.Should().Be(jsonType);
            columns[Named(sqlServer, json)].IsNullable.Should().BeFalse();
        }

        columns.Values.Where(x => x.IsNullable).Select(x => x.Name)
            .Should().BeEquivalentTo(
                new[] { "revoked_time", "revoked_by_user_id", "last_token_issued_time", "created_by_user_id", "modified_by_user_id", "modified_time" }
                    .Select(x => Named(sqlServer, x)),
                "a new grant is unrevoked and has issued no token");
        columns[Named(sqlServer, "name")].MaxLength.Should().Be(100);
        columns.Values.Should().OnlyContain(x => x.DefaultValue == null && x.DefaultValueSql == null);
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
