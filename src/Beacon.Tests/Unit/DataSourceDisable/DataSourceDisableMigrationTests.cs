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

namespace Beacon.Tests.Unit.DataSourceDisable;

/// <summary>
/// The data-source disable migration, checked offline on both providers (nothing connects, nothing is migrated): it adds
/// the nullable disabled time and disabled-by user id to the data sources, with no default, so every existing source stays
/// enabled, and runs no SQL. Down removes exactly what Up adds. Its target model is the snapshot, the model has no pending
/// changes, and it sorts last.
/// </summary>
[TestFixture]
public class DataSourceDisableMigrationTests
{
    private const string Suffix = "_AddDataSourceDisable";

    [TestCase(false, "data_sources", "disabled_time", "timestamp with time zone", "disabled_by_user_id", "integer")]
    [TestCase(true, "DataSources", "DisabledTime", "datetime2", "DisabledByUserId", "int")]
    public void TheMigration_AddsTwoNullableColumns_WithoutSql_AndItsTargetModelIsTheSnapshot(
        bool sqlServer,
        string table,
        string disabledTime,
        string timeType,
        string disabledBy,
        string userIdType)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();

        var migration = MigrationNamed(context, migrations, Suffix);

        migration.UpOperations.Should().NotContain(x => x is SqlOperation, "nothing rewrites or backfills a stored row");
        var added = migration.UpOperations.Should().AllBeOfType<AddColumnOperation>().Subject.ToList();
        added.Select(x => $"{x.Table}.{x.Name} {x.ColumnType}")
            .Should().BeEquivalentTo([$"{table}.{disabledTime} {timeType}", $"{table}.{disabledBy} {userIdType}"]);
        added.Should().OnlyContain(x => x.IsNullable && x.DefaultValue == null && x.DefaultValueSql == null, "every existing source stays enabled");
        migration.DownOperations.OfType<DropColumnOperation>().Select(x => $"{x.Table}.{x.Name}")
            .Should().BeEquivalentTo(added.Select(x => $"{x.Table}.{x.Name}"), "Down drops exactly the columns Up adds");
        migration.DownOperations.Should().AllBeOfType<DropColumnOperation>();
        var previous = MigrationNamed(context, migrations, "_AddSecurityAuditTrail");
        var target = Initialized(migration.TargetModel!, initializer).GetRelationalModel();
        context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(Initialized(previous.TargetModel!, initializer).GetRelationalModel(), target)
            .Select(x => x is AddColumnOperation column ? $"{column.Table}.{column.Name} {column.ColumnType} {column.IsNullable}" : x.GetType().Name)
            .Should().BeEquivalentTo(
                added.Select(x => $"{x.Table}.{x.Name} {x.ColumnType} {x.IsNullable}"),
                "the target model differs from the one before only by the columns this migration adds");
        context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(target, Initialized(migrations.ModelSnapshot!.Model, initializer).GetRelationalModel())
            .Should().BeEmpty("the migration's target model is the snapshot");
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        migrations.Migrations.Keys
            .Order(StringComparer.Ordinal)
            .Last()
            .Should().EndWith(Suffix, "it sorts after every earlier migration");
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
