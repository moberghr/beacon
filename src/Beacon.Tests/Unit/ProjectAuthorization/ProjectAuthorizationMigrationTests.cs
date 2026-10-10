using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
/// linking user and change-history source are nullable columns that existing rows leave empty.
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
        migrations.Migrations.Keys.Order(StringComparer.Ordinal).Last().Should().Be(migrations.Migrations.Keys.Single(x => x.EndsWith("_AddProjectAuthorization", StringComparison.Ordinal)), "it sorts after every earlier migration");
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
        var id = migrations.Migrations.Keys.Single(x => x.EndsWith("_AddProjectAuthorization", StringComparison.Ordinal));

        return migrations.CreateMigration(migrations.Migrations[id], context.Database.ProviderName!);
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
