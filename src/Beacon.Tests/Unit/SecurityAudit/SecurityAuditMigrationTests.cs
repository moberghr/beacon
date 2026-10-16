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

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The security audit migration, checked offline on both providers (nothing connects, nothing is migrated): it creates the
/// security audit table and the MCP audit statement table (cascading from its audit row), adds the MCP audit content hash
/// and the attribution columns of queries, subscriptions, recipients, data contracts and migration jobs — every added
/// column nullable, with no default, so existing rows are left as they are — and runs no SQL. Down removes exactly what Up
/// adds. The next migration builds on its target model, the model has no pending changes, and it sorts after the grounding-provenance migration.
/// </summary>
[TestFixture]
public class SecurityAuditMigrationTests
{
    private const string Suffix = "_AddSecurityAuditTrail";

    [TestCase(false, "security_audit_events", "mcp_audit_statements", "mcp_audit_logs", "content_hash", "fk_mcp_audit_statements_mcp_audit_logs_mcp_audit_log_id")]
    [TestCase(true, "SecurityAuditEvents", "McpAuditStatements", "McpAuditLogs", "ContentHash", "FK_McpAuditStatements_McpAuditLogs_McpAuditLogId")]
    public void TheMigration_CreatesTwoTablesAndAddsNullableColumns_WithoutSql_AndTheNextMigrationBuildsOnItsTargetModel(
        bool sqlServer,
        string events,
        string statements,
        string auditLogs,
        string contentHash,
        string statementKey)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();

        var migration = MigrationNamed(context, migrations, Suffix);

        migration.UpOperations.Should().NotContain(x => x is SqlOperation, "nothing rewrites or backfills a stored row");
        migration.UpOperations.Should().OnlyContain(x => x is AddColumnOperation || x is CreateTableOperation || x is CreateIndexOperation);
        var added = migration.UpOperations.OfType<AddColumnOperation>().ToList();
        added.Should().HaveCount(16, "the content hash and three attribution columns on five tables");
        added.Should().OnlyContain(x => x.IsNullable && x.DefaultValue == null && x.DefaultValueSql == null, "existing rows keep their values");
        added.Should().ContainSingle(x => x.Table == auditLogs && x.Name == contentHash);
        added.Select(x => x.Table)
            .Distinct()
            .Should().HaveCount(6);
        var tables = migration.UpOperations.OfType<CreateTableOperation>().ToDictionary(x => x.Name);
        tables.Keys.Should().BeEquivalentTo([events, statements]);
        tables[events].ForeignKeys.Should().BeEmpty("an event outlives the user, key or project it names");
        tables[statements].ForeignKeys.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Name = statementKey, PrincipalTable = auditLogs, OnDelete = ReferentialAction.Cascade }, x => x.ExcludingMissingMembers());
        migration.UpOperations.OfType<CreateIndexOperation>()
            .Should().ContainSingle(x => x.Table == events && x.IsUnique, "the event guid is unique")
            .Which.Columns.Should().ContainSingle();
        migration.UpOperations.OfType<CreateIndexOperation>().Should().HaveCount(5);
        migration.DownOperations.OfType<DropTableOperation>().Select(x => x.Name).Should().BeEquivalentTo([events, statements]);
        migration.DownOperations.OfType<DropColumnOperation>().Select(x => $"{x.Table}.{x.Name}")
            .Should().BeEquivalentTo(added.Select(x => $"{x.Table}.{x.Name}"), "Down drops exactly the columns Up adds");
        var previous = MigrationNamed(context, migrations, "_AddGroundingProvenance");
        var target = Initialized(migration.TargetModel!, initializer).GetRelationalModel();
        var differences = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(Initialized(previous.TargetModel!, initializer).GetRelationalModel(), target);
        differences.OfType<AddColumnOperation>().Select(x => $"{x.Table}.{x.Name} {x.ColumnType} {x.IsNullable}")
            .Should().BeEquivalentTo(
                added.Select(x => $"{x.Table}.{x.Name} {x.ColumnType} {x.IsNullable}"),
                "the migration's Up adds exactly the columns its target model adds");
        differences.OfType<CreateTableOperation>().Select(x => x.Name).Should().BeEquivalentTo([events, statements]);
        foreach (var table in differences.OfType<CreateTableOperation>())
        {
            table.Columns.Select(x => $"{x.Name} {x.ColumnType} {x.IsNullable}")
                .Should().BeEquivalentTo(tables[table.Name].Columns.Select(x => $"{x.Name} {x.ColumnType} {x.IsNullable}"), "the migration creates the table its target model holds");
        }

        differences.OfType<CreateIndexOperation>().Select(x => $"{x.Table}({string.Join(",", x.Columns)}) {x.IsUnique}")
            .Should().BeEquivalentTo(migration.UpOperations.OfType<CreateIndexOperation>().Select(x => $"{x.Table}({string.Join(",", x.Columns)}) {x.IsUnique}"));
        differences.Should().HaveCount(migration.UpOperations.Count, "the target model differs from the one before only by this migration");
        var next = MigrationNamed(context, migrations, "_AddDataSourceDisable");
        context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(target, Initialized(next.TargetModel!, initializer).GetRelationalModel())
            .Select(x => x.GetType().Name)
            .Should().BeEquivalentTo(
                next.UpOperations.Select(x => x.GetType().Name),
                "the next migration's target model differs from its target model only by that migration");
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        migrations.Migrations.Keys
            .Order(StringComparer.Ordinal)
            .SkipWhile(x => !x.EndsWith("_AddGroundingProvenance", StringComparison.Ordinal))
            .Skip(1)
            .First()
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
