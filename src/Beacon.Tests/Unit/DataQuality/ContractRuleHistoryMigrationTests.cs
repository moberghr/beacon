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
        differ.GetDifferences(target, Initialized(migrations.ModelSnapshot!.Model, initializer).GetRelationalModel())
            .Should().BeEmpty("the migration's target model is the snapshot");
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        migrations.Migrations.Keys
            .Order(StringComparer.Ordinal)
            .Last()
            .Should().EndWith(Suffix, "it sorts after every earlier migration");
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
