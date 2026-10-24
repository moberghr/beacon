using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.PostgreSql;
using Beacon.Core.SqlServer;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Documentation;

/// <summary>
/// The migration that records who edited a documentation section and when, checked offline on both providers (nothing
/// connects, nothing is migrated): it adds two nullable columns to the documentation sections (every existing section
/// stays generated text) and runs no SQL, Up is exactly the difference between the model before it and its target model,
/// and Down drops exactly those columns. Its target model is the snapshot, the model has no pending changes, and it sorts
/// last.
/// </summary>
[TestFixture]
public class DocumentationSectionEditsMigrationTests
{
    private const string Suffix = "_AddDocumentationSectionEdits";

    [TestCase(false)]
    [TestCase(true)]
    public void TheMigration_AddsTheNullableEditColumns_AndDownDropsThem(bool sqlServer)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var differ = context.GetService<IMigrationsModelDiffer>();

        var migration = MigrationNamed(context, migrations, Suffix);
        var previous = MigrationNamed(context, migrations, "_AddContractRuleHistory");
        var before = Initialized(previous.TargetModel!, initializer).GetRelationalModel();
        var target = Initialized(migration.TargetModel!, initializer).GetRelationalModel();
        var table = sqlServer ? "ProjectDocumentationSections" : "project_documentation_sections";
        string[] columns = sqlServer ? ["EditedByUserId", "EditedTime"] : ["edited_by_user_id", "edited_time"];

        migration.UpOperations.Should().NotContain(x => x is SqlOperation, "nothing rewrites or backfills a stored row");
        migration.DownOperations.Should().NotContain(x => x is SqlOperation);
        var added = migration.UpOperations.Should().AllBeOfType<AddColumnOperation>().Subject.ToList();
        added.Select(x => $"{x.Schema}.{x.Table}.{x.Name}").Should().BeEquivalentTo(columns.Select(x => $"beacon.{table}.{x}"));
        added.Should().OnlyContain(x => x.IsNullable, "an existing section has no edit");
        added.Select(x => x.ClrType).Should().BeEquivalentTo([typeof(int), typeof(DateTime)]);
        migration.DownOperations.Should().AllBeOfType<DropColumnOperation>()
            .Which.Select(x => $"{x.Schema}.{x.Table}.{x.Name}").Should().BeEquivalentTo(columns.Select(x => $"beacon.{table}.{x}"));
        Signatures(migration.UpOperations)
            .Should().BeEquivalentTo(Signatures(differ.GetDifferences(before, target)), "Up is exactly the difference from the model before");
        Signatures(migration.DownOperations)
            .Should().BeEquivalentTo(Signatures(differ.GetDifferences(target, before)), "Down removes exactly what Up adds");
        // The next migration's own content is pinned by its own tests.
        var next = MigrationNamed(context, migrations, "_AddMcpAuditSeal");
        Signatures(differ.GetDifferences(target, Initialized(next.TargetModel!, initializer).GetRelationalModel()))
            .Should().BeEquivalentTo(
                Signatures(next.UpOperations),
                "this migration's target model differs from the next one's only by the next migration");
        migrations.Migrations.Keys
            .Order(StringComparer.Ordinal)
            .SkipWhile(x => !x.EndsWith(Suffix, StringComparison.Ordinal))
            .Skip(1)
            .First()
            .Should().EndWith("_AddMcpAuditSeal", "the next migration sorts right after it");
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

    private static List<string> Signatures(IEnumerable<MigrationOperation> operations)
    {
        var signatures = new List<string>();
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case AddColumnOperation column:
                    signatures.Add($"add {column.Schema}.{column.Table}.{column.Name} {column.ColumnType} null={column.IsNullable} default={column.DefaultValue}");
                    break;
                case DropColumnOperation column:
                    signatures.Add($"drop {column.Schema}.{column.Table}.{column.Name}");
                    break;
                default:
                    signatures.Add(operation.GetType().Name);
                    break;
            }
        }

        return signatures;
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
