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

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// The project authorization migration, checked offline on both providers (nothing connects, nothing is migrated): the
/// model has no pending changes against its snapshot, existing data sources stay reachable while unlinked
/// (<c>open_when_unlinked</c> defaults to true for existing rows), existing projects stay open, and the recorded owner,
/// linking user and change-history source are nullable columns that existing rows leave empty. The later contract-owner
/// migration adds one nullable timestamp that existing contracts leave empty. The last one marks no data source as a
/// migration write target: it adds one boolean that existing rows leave false, and its target model is the snapshot.
/// </summary>
[TestFixture]
public class ProjectAuthorizationMigrationTests
{
    [TestCase(false, "data_sources.open_when_unlinked", "projects.requires_membership")]
    [TestCase(true, "DataSources.OpenWhenUnlinked", "Projects.RequiresMembership")]
    public void TheMigration_KeepsExistingSourcesOpenUnlinked_AndExistingProjectsOpen(bool sqlServer, string openWhenUnlinked, string requiresMembership)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();

        var migration = ProjectAuthorizationMigration(context, migrations);
        var added = migration.UpOperations.OfType<AddColumnOperation>().ToDictionary(x => $"{x.Table}.{x.Name}");

        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        added[openWhenUnlinked].DefaultValue.Should().Be(true);
        added[requiresMembership].DefaultValue.Should().Be(false);
        migration.UpOperations.OfType<CreateTableOperation>().Should().ContainSingle()
            .Which.PrimaryKey!.Name.Should().Be("pk_project_members");
        Ordered(migrations)
            .SkipWhile(x => !x.EndsWith("_AddProjectAuthorization", StringComparison.Ordinal))
            .Skip(1)
            .Should().Equal(
                [Ordered(migrations).Single(x => x.EndsWith("_AddContractOwnerNotAuthorized", StringComparison.Ordinal)), Ordered(migrations).Single(x => x.EndsWith("_AddDataSourceMigrationWrites", StringComparison.Ordinal))],
                "it sorts after every earlier migration, and only the contract-owner and migration-writes migrations follow it");
    }

    [TestCase(false, "data_contracts", "owner_not_authorized_since", "timestamp with time zone")]
    [TestCase(true, "DataContracts", "OwnerNotAuthorizedSince", "datetime2")]
    public void TheContractOwnerMigration_AddsOneNullableTimestamp_AndItsTargetModelIsTheSnapshotBeforeTheNextOne(bool sqlServer, string table, string column, string type)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();

        var migration = MigrationNamed(context, migrations, "_AddContractOwnerNotAuthorized");

        migration.UpOperations.Should().ContainSingle()
            .Which.Should().BeOfType<AddColumnOperation>()
            .Which.Should().BeEquivalentTo(new { Schema = "beacon", Table = table, Name = column, ColumnType = type, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null }, x => x.ExcludingMissingMembers());
        migration.DownOperations.Should().ContainSingle()
            .Which.Should().BeOfType<DropColumnOperation>()
            .Which.Should().BeEquivalentTo(new { Schema = "beacon", Table = table, Name = column }, x => x.ExcludingMissingMembers());
        context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(Initialized(migration.TargetModel!, initializer).GetRelationalModel(), Initialized(migrations.ModelSnapshot!.Model, initializer).GetRelationalModel())
            .Should().ContainSingle("the snapshot differs from its target model only by the migration that follows it")
            .Which.Should().BeOfType<AddColumnOperation>()
            .Which.Name.Should().Be(sqlServer ? "AllowMigrationWrites" : "allow_migration_writes");
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        Ordered(migrations).SkipLast(1).Last().Should().EndWith("_AddContractOwnerNotAuthorized", "it sorts after every earlier migration");
    }

    [TestCase(false, "data_sources", "allow_migration_writes", "boolean")]
    [TestCase(true, "DataSources", "AllowMigrationWrites", "bit")]
    public void TheMigrationWritesMigration_AddsOneBooleanThatExistingSourcesLeaveFalse_AndItsTargetModelIsTheSnapshot(bool sqlServer, string table, string column, string type)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();

        var migration = MigrationNamed(context, migrations, "_AddDataSourceMigrationWrites");

        migration.UpOperations.Should().ContainSingle()
            .Which.Should().BeOfType<AddColumnOperation>()
            .Which.Should().BeEquivalentTo(new { Schema = "beacon", Table = table, Name = column, ColumnType = type, IsNullable = false, DefaultValue = (object?)false, DefaultValueSql = (string?)null }, x => x.ExcludingMissingMembers());
        migration.DownOperations.Should().ContainSingle()
            .Which.Should().BeOfType<DropColumnOperation>()
            .Which.Should().BeEquivalentTo(new { Schema = "beacon", Table = table, Name = column }, x => x.ExcludingMissingMembers());
        context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(Initialized(migration.TargetModel!, initializer).GetRelationalModel(), Initialized(migrations.ModelSnapshot!.Model, initializer).GetRelationalModel())
            .Should().BeEmpty("the migration's target model is the snapshot");
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        Ordered(migrations).Last().Should().EndWith("_AddDataSourceMigrationWrites", "it sorts after every earlier migration");
    }

    [TestCase(false, "queries.owner_user_id", "character varying(200)", "project_data_sources.linked_by_user_id", "query_step_change_history.data_source_id", "integer")]
    [TestCase(true, "Queries.OwnerUserId", "nvarchar(200)", "ProjectDataSources.LinkedByUserId", "QueryStepChangeHistory.DataSourceId", "int")]
    public void TheMigration_AddsQueryOwnerLinkingUserAndHistorySource_AsNullableColumnsWithoutKeys(
        bool sqlServer,
        string queryOwner,
        string ownerType,
        string linkedBy,
        string historySource,
        string intType)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migration = ProjectAuthorizationMigration(context, context.GetService<IMigrationsAssembly>());

        var added = migration.UpOperations.OfType<AddColumnOperation>().ToDictionary(x => $"{x.Table}.{x.Name}");
        var dropped = migration.DownOperations.OfType<DropColumnOperation>().Select(x => $"{x.Table}.{x.Name}").ToList();

        added[queryOwner].Should().BeEquivalentTo(new { ColumnType = ownerType, MaxLength = (int?)200, IsNullable = true, DefaultValue = (object?)null }, x => x.ExcludingMissingMembers());
        added[linkedBy].Should().BeEquivalentTo(new { ColumnType = intType, IsNullable = true, DefaultValue = (object?)null }, x => x.ExcludingMissingMembers());
        added[historySource].Should().BeEquivalentTo(new { ColumnType = intType, IsNullable = true, DefaultValue = (object?)null }, x => x.ExcludingMissingMembers());
        dropped.Should().Contain([queryOwner, linkedBy, historySource]);
        migration.UpOperations.OfType<AddForeignKeyOperation>().Should().BeEmpty("the linking user and the history source are recorded ids, not keys");
        migration.UpOperations.OfType<CreateIndexOperation>().Select(x => x.Table).Should().NotContain([TableOf(queryOwner), TableOf(linkedBy), TableOf(historySource)]);
    }

    private static Migration ProjectAuthorizationMigration(BeaconContext context, IMigrationsAssembly migrations)
    {
        return MigrationNamed(context, migrations, "_AddProjectAuthorization");
    }

    private static Migration MigrationNamed(BeaconContext context, IMigrationsAssembly migrations, string suffix)
    {
        var id = migrations.Migrations.Keys.Single(x => x.EndsWith(suffix, StringComparison.Ordinal));

        return migrations.CreateMigration(migrations.Migrations[id], context.Database.ProviderName!);
    }

    private static List<string> Ordered(IMigrationsAssembly migrations)
    {
        return migrations.Migrations.Keys
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    // A model built by a migration or a snapshot, made ready for the differ as the migrator makes it (before anything
    // else has initialized it).
    private static IModel Initialized(IModel model, IModelRuntimeInitializer initializer)
    {
        var finalized = model is IMutableModel mutable ? mutable.FinalizeModel() : model;

        return initializer.Initialize(finalized, designTime: true, validationLogger: null);
    }

    private static string TableOf(string column)
    {
        return column[..column.IndexOf('.')];
    }

    private static ServiceProvider BuildProvider(bool sqlServer)
    {
        var services = new ServiceCollection();
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:EncryptionKey"] = Convert.ToBase64String(new byte[32]) })
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
