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

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// The project authorization migration, checked offline on both providers (nothing connects, nothing is migrated): the
/// model has no pending changes against its snapshot, existing data sources stay reachable while unlinked
/// (<c>open_when_unlinked</c> defaults to true for existing rows), existing projects stay open, and the recorded owner,
/// linking user and change-history source are nullable columns that existing rows leave empty. The later contract-owner
/// migration adds one nullable timestamp that existing contracts leave empty. The migration-writes migration marks no data
/// source as a migration write target: it adds one boolean that existing rows leave false. The last one adds the users'
/// security stamp, one nullable column that existing users leave empty, and the flag that a user must choose a new
/// password, which existing users leave false. The next one only changes the default of the global MCP settings' sample
/// value collection to off, for a row created from then on; it rewrites no stored row. The next one adds the query change
/// integrity columns — a version's content hash, a request's base version, approved hash and contributors — all
/// nullable, which existing rows leave empty. The next one creates the change request table and adds the approved hash
/// of migration jobs, Custom SQL rules and recipients, nullable, which existing rows leave empty (not governed), and two
/// counts on data-quality evaluations, which existing evaluations leave at zero. The next one adds who promoted and who
/// last edited a golden case and who rated a query signal, three nullable ids that existing rows leave empty, and when and to
/// which case a signal was promoted. The last one adds whether a verdict came from a curator, a boolean existing verdicts
/// leave false, and the digests of a signal's promotable SQL and of a learned pattern's content, nullable, which existing
/// rows leave empty. The security audit migration that follows is checked by <c>SecurityAuditMigrationTests</c>. Each
/// earlier migration's target model differs from the next one's only by that next migration.
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
                [IdEndingWith(migrations, "_AddContractOwnerNotAuthorized"), IdEndingWith(migrations, "_AddDataSourceMigrationWrites"), IdEndingWith(migrations, "_AddUserSecurityStamp"), IdEndingWith(migrations, "_DefaultSampleValueCollectionOff"), IdEndingWith(migrations, "_AddQueryChangeIntegrity"), IdEndingWith(migrations, "_AddChangeApprovalRequests"), IdEndingWith(migrations, "_AddGroundingAttribution"), IdEndingWith(migrations, "_AddGroundingProvenance"), IdEndingWith(migrations, "_AddSecurityAuditTrail"), IdEndingWith(migrations, "_AddDataSourceDisable"), IdEndingWith(migrations, "_RemoveUnusedFeatures"), IdEndingWith(migrations, "_AddRowSecurityModel"), IdEndingWith(migrations, "_AddConnectionChangeApproval"), IdEndingWith(migrations, "_AddUserSignInLockout"), IdEndingWith(migrations, "_AddUserSignInFailureTime"), IdEndingWith(migrations, "_AddBackgroundTasks"), IdEndingWith(migrations, "_AddEmbedGrants"), IdEndingWith(migrations, "_AddBackgroundTaskGrantAndHeartbeat"), IdEndingWith(migrations, "_AddEmbeddingContentHash")],
                "it sorts after every earlier migration, and only the contract-owner, migration-writes, security-stamp, sample-default, change-integrity, change-request, grounding-attribution, grounding-provenance, security-audit, data-source-disable, unused-feature removal, row-security model, connection-change approval, sign-in lockout, sign-in failure time, background-task, embed-grant, background-task grant and embedding content hash migrations follow it");
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
        var next = MigrationNamed(context, migrations, "_AddDataSourceMigrationWrites");
        context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(Initialized(migration.TargetModel!, initializer).GetRelationalModel(), Initialized(next.TargetModel!, initializer).GetRelationalModel())
            .Should().ContainSingle("the next migration's target model differs from its target model only by that migration")
            .Which.Should().BeOfType<AddColumnOperation>()
            .Which.Name.Should().Be(sqlServer ? "AllowMigrationWrites" : "allow_migration_writes");
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        NextAfter(migrations, "_AddContractOwnerNotAuthorized").Should().EndWith("_AddDataSourceMigrationWrites", "it sorts after every earlier migration");
    }

    [TestCase(false, "data_sources", "allow_migration_writes", "boolean")]
    [TestCase(true, "DataSources", "AllowMigrationWrites", "bit")]
    public void TheMigrationWritesMigration_AddsOneBooleanThatExistingSourcesLeaveFalse_AndItsTargetModelIsTheOneBeforeTheNext(bool sqlServer, string table, string column, string type)
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
        var next = MigrationNamed(context, migrations, "_AddUserSecurityStamp");
        context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(Initialized(migration.TargetModel!, initializer).GetRelationalModel(), Initialized(next.TargetModel!, initializer).GetRelationalModel())
            .Should().AllBeOfType<AddColumnOperation>("the next migration's target model differs from its target model only by that migration")
            .Which.Select(x => x.Name).Should().Equal(sqlServer ? new[] { "MustChangePassword", "SecurityStamp" } : new[] { "must_change_password", "security_stamp" });
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        NextAfter(migrations, "_AddDataSourceMigrationWrites").Should().EndWith("_AddUserSecurityStamp", "it sorts after every earlier migration");
    }

    [TestCase(false, "users", "security_stamp", "character varying(64)", "must_change_password", "boolean")]
    [TestCase(true, "Users", "SecurityStamp", "nvarchar(64)", "MustChangePassword", "bit")]
    public void TheSecurityStampMigration_AddsANullableStampThatExistingUsersLeaveEmpty_AndAPasswordChangeFlagTheyLeaveFalse_AndItsTargetModelIsTheOneBeforeTheNext(
        bool sqlServer,
        string table,
        string column,
        string type,
        string flagColumn,
        string flagType)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();

        var migration = MigrationNamed(context, migrations, "_AddUserSecurityStamp");

        var added = migration.UpOperations.Should().HaveCount(2).And.AllBeOfType<AddColumnOperation>().Which.ToList();
        added[0].Should().BeEquivalentTo(new { Schema = "beacon", Table = table, Name = flagColumn, ColumnType = flagType, IsNullable = false, DefaultValue = (object?)false, DefaultValueSql = (string?)null }, x => x.ExcludingMissingMembers());
        added[1].Should().BeEquivalentTo(new { Schema = "beacon", Table = table, Name = column, ColumnType = type, MaxLength = (int?)64, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null }, x => x.ExcludingMissingMembers());
        migration.DownOperations.Should().AllBeOfType<DropColumnOperation>()
            .Which.Should().BeEquivalentTo(
                new[] { new { Schema = "beacon", Table = table, Name = flagColumn }, new { Schema = "beacon", Table = table, Name = column } },
                x => x.ExcludingMissingMembers().WithStrictOrdering());
        var next = MigrationNamed(context, migrations, "_DefaultSampleValueCollectionOff");
        context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(Initialized(migration.TargetModel!, initializer).GetRelationalModel(), Initialized(next.TargetModel!, initializer).GetRelationalModel())
            .Should().ContainSingle("the next migration's target model differs from its target model only by that migration")
            .Which.Should().BeOfType<AlterColumnOperation>();
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        NextAfter(migrations, "_AddUserSecurityStamp").Should().EndWith("_DefaultSampleValueCollectionOff", "it sorts after every earlier migration");
    }

    // New installations start with sample-value collection off: only the column default changes, nothing rewrites the
    // stored settings row, so an existing installation keeps the value it has.
    [TestCase(false, "mcp_settings", "enable_sample_value_collection", "boolean")]
    [TestCase(true, "McpSettings", "EnableSampleValueCollection", "bit")]
    public void TheSampleDefaultMigration_OnlyTurnsTheColumnDefaultOff_AndItsTargetModelIsTheOneBeforeTheNext(bool sqlServer, string table, string column, string type)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var previous = MigrationNamed(context, migrations, "_AddUserSecurityStamp");

        var migration = MigrationNamed(context, migrations, "_DefaultSampleValueCollectionOff");

        var up = migration.UpOperations.Should().ContainSingle().Which.Should().BeOfType<AlterColumnOperation>().Subject;
        up.Should().BeEquivalentTo(new { Schema = "beacon", Table = table, Name = column, ColumnType = type, IsNullable = false, DefaultValue = (object?)false, DefaultValueSql = (string?)null }, x => x.ExcludingMissingMembers());
        up.OldColumn.Should().BeEquivalentTo(new { ColumnType = type, IsNullable = false, DefaultValue = (object?)true }, x => x.ExcludingMissingMembers());
        var down = migration.DownOperations.Should().ContainSingle().Which.Should().BeOfType<AlterColumnOperation>().Subject;
        down.DefaultValue.Should().Be(true);
        down.OldColumn.DefaultValue.Should().Be(false);
        var target = Initialized(migration.TargetModel!, initializer).GetRelationalModel();
        var differences = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(Initialized(previous.TargetModel!, initializer).GetRelationalModel(), target);
        differences.Should().ContainSingle("the migration's target model differs from the one before only by the default")
            .Which.Should().BeOfType<AlterColumnOperation>()
            .Which.Should().BeEquivalentTo(new { Table = table, Name = column, DefaultValue = (object?)false }, x => x.ExcludingMissingMembers());
        var next = MigrationNamed(context, migrations, "_AddQueryChangeIntegrity");
        context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(target, Initialized(next.TargetModel!, initializer).GetRelationalModel())
            .Should().HaveCount(4, "the next migration's target model differs from its target model only by that migration")
            .And.AllBeOfType<AddColumnOperation>();
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        NextAfter(migrations, "_DefaultSampleValueCollectionOff").Should().EndWith("_AddQueryChangeIntegrity", "it sorts after every earlier migration");
    }

    // The query change integrity columns: additive and nullable, so existing versions keep no hash (computed from their
    // snapshot when needed) and existing requests no base, approved hash or contributors. No index, no key: the base
    // version is a recorded id, the contributors recorded ids.
    [TestCase(false, "query_versions", "content_hash", "query_approval_requests", "approved_content_hash", "base_version_id", "contributor_user_ids", "character varying(64)", "integer", "text[]")]
    [TestCase(true, "QueryVersions", "ContentHash", "QueryApprovalRequests", "ApprovedContentHash", "BaseVersionId", "ContributorUserIds", "nvarchar(64)", "int", "nvarchar(max)")]
    public void TheChangeIntegrityMigration_AddsFourNullableColumns_AndItsTargetModelIsTheOneBeforeTheNext(
        bool sqlServer,
        string versions,
        string contentHash,
        string requests,
        string approvedHash,
        string baseVersion,
        string contributors,
        string hashType,
        string intType,
        string idsType)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var previous = MigrationNamed(context, migrations, "_DefaultSampleValueCollectionOff");

        var migration = MigrationNamed(context, migrations, "_AddQueryChangeIntegrity");

        migration.UpOperations.Should().AllBeOfType<AddColumnOperation>().Which.Should().BeEquivalentTo(
            new[]
            {
                new { Schema = "beacon", Table = versions, Name = contentHash, ColumnType = hashType, MaxLength = (int?)64, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null },
                new { Schema = "beacon", Table = requests, Name = approvedHash, ColumnType = hashType, MaxLength = (int?)64, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null },
                new { Schema = "beacon", Table = requests, Name = baseVersion, ColumnType = intType, MaxLength = (int?)null, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null },
                new { Schema = "beacon", Table = requests, Name = contributors, ColumnType = idsType, MaxLength = (int?)null, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null }
            },
            x => x.ExcludingMissingMembers().WithStrictOrdering());
        migration.DownOperations.Should().AllBeOfType<DropColumnOperation>().Which.Should().BeEquivalentTo(
            new[]
            {
                new { Schema = "beacon", Table = versions, Name = contentHash },
                new { Schema = "beacon", Table = requests, Name = approvedHash },
                new { Schema = "beacon", Table = requests, Name = baseVersion },
                new { Schema = "beacon", Table = requests, Name = contributors }
            },
            x => x.ExcludingMissingMembers().WithStrictOrdering());
        var target = Initialized(migration.TargetModel!, initializer).GetRelationalModel();
        context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(Initialized(previous.TargetModel!, initializer).GetRelationalModel(), target)
            .Should().AllBeOfType<AddColumnOperation>()
            .Which.Select(x => $"{x.Table}.{x.Name} {x.ColumnType} {x.IsNullable}")
            .Should().BeEquivalentTo(
                migration.UpOperations.OfType<AddColumnOperation>().Select(x => $"{x.Table}.{x.Name} {x.ColumnType} {x.IsNullable}"),
                "the migration's Up is exactly what its target model adds");
        var next = MigrationNamed(context, migrations, "_AddChangeApprovalRequests");
        context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(target, Initialized(next.TargetModel!, initializer).GetRelationalModel())
            .Select(x => x.GetType().Name)
            .Should().BeEquivalentTo(
                next.UpOperations.Select(x => x.GetType().Name),
                "the next migration's target model differs from its target model only by that migration");
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        NextAfter(migrations, "_DefaultSampleValueCollectionOff").Should().EndWith("_AddQueryChangeIntegrity", "it sorts after every earlier migration");
    }

    // The change requests of migration jobs, Custom SQL rules and recipients: a new table, and one nullable approved hash on
    // each governed table, which existing rows leave empty — the workflow reads that as not governed, so they work as
    // before. No key: a request names its resource by kind and id, and outlives it. Evaluations gain two counts (rules left
    // out as waiting for approval, alert recipients held), which existing evaluations leave at zero.
    [TestCase(false, "change_approval_requests", "migration_jobs", "approved_definition_hash", "data_contract_rules", "approved_sql_hash", "recipients", "approved_destination_hash", "character varying(64)", "data_quality_evaluations", "awaiting_approval_rules", "held_alert_recipients", "integer")]
    [TestCase(true, "ChangeApprovalRequests", "MigrationJobs", "ApprovedDefinitionHash", "DataContractRules", "ApprovedSqlHash", "Recipients", "ApprovedDestinationHash", "nvarchar(64)", "DataQualityEvaluations", "AwaitingApprovalRules", "HeldAlertRecipients", "int")]
    public void TheChangeRequestMigration_CreatesTheRequestTable_AddsThreeNullableHashesAndTwoEvaluationCounts_AndItsTargetModelIsTheOneBeforeTheNext(
        bool sqlServer,
        string requests,
        string jobs,
        string jobHash,
        string rules,
        string ruleHash,
        string recipients,
        string recipientHash,
        string hashType,
        string evaluations,
        string awaitingRules,
        string heldRecipients,
        string intType)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var previous = MigrationNamed(context, migrations, "_AddQueryChangeIntegrity");

        var migration = MigrationNamed(context, migrations, "_AddChangeApprovalRequests");

        migration.UpOperations.OfType<AddColumnOperation>().Should().BeEquivalentTo(
            new[]
            {
                new { Schema = "beacon", Table = jobs, Name = jobHash, ColumnType = hashType, MaxLength = (int?)64, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null },
                new { Schema = "beacon", Table = rules, Name = ruleHash, ColumnType = hashType, MaxLength = (int?)64, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null },
                new { Schema = "beacon", Table = recipients, Name = recipientHash, ColumnType = hashType, MaxLength = (int?)64, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null },
                new { Schema = "beacon", Table = evaluations, Name = awaitingRules, ColumnType = intType, MaxLength = (int?)null, IsNullable = false, DefaultValue = (object?)0, DefaultValueSql = (string?)null },
                new { Schema = "beacon", Table = evaluations, Name = heldRecipients, ColumnType = intType, MaxLength = (int?)null, IsNullable = false, DefaultValue = (object?)0, DefaultValueSql = (string?)null }
            },
            x => x.ExcludingMissingMembers().WithStrictOrdering());
        var table = migration.UpOperations.OfType<CreateTableOperation>().Should().ContainSingle().Subject;
        table.Should().BeEquivalentTo(new { Schema = "beacon", Name = requests }, x => x.ExcludingMissingMembers());
        table.ForeignKeys.Should().BeEmpty("a request names its resource by kind and id, not by a key");
        migration.UpOperations.OfType<CreateIndexOperation>().Should().HaveCount(2).And.OnlyContain(x => x.Table == requests);
        migration.UpOperations.Should().HaveCount(8, "five columns, the table and its two indexes; nothing rewrites a stored row");
        migration.DownOperations.OfType<DropTableOperation>().Should().ContainSingle().Which.Name.Should().Be(requests);
        migration.DownOperations.OfType<DropColumnOperation>().Select(x => $"{x.Table}.{x.Name}")
            .Should().BeEquivalentTo([$"{jobs}.{jobHash}", $"{rules}.{ruleHash}", $"{recipients}.{recipientHash}", $"{evaluations}.{awaitingRules}", $"{evaluations}.{heldRecipients}"]);
        var target = Initialized(migration.TargetModel!, initializer).GetRelationalModel();
        var differences = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(Initialized(previous.TargetModel!, initializer).GetRelationalModel(), target);
        differences.OfType<AddColumnOperation>().Select(x => $"{x.Table}.{x.Name} {x.ColumnType} {x.IsNullable}")
            .Should().BeEquivalentTo(
                migration.UpOperations.OfType<AddColumnOperation>().Select(x => $"{x.Table}.{x.Name} {x.ColumnType} {x.IsNullable}"),
                "the migration's Up adds exactly the columns its target model adds");
        differences.OfType<CreateTableOperation>().Should().ContainSingle()
            .Which.Columns.Select(x => $"{x.Name} {x.ColumnType} {x.IsNullable}")
            .Should().BeEquivalentTo(table.Columns.Select(x => $"{x.Name} {x.ColumnType} {x.IsNullable}"), "the migration creates the table its target model holds");
        differences.Should().HaveCount(8, "the target model differs from the one before only by this migration");
        var next = MigrationNamed(context, migrations, "_AddGroundingAttribution");
        context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(target, Initialized(next.TargetModel!, initializer).GetRelationalModel())
            .Select(x => x.GetType().Name)
            .Should().BeEquivalentTo(
                next.UpOperations.Where(x => x is not SqlOperation).Select(x => x.GetType().Name),
                "the next migration's target model differs from its target model only by that migration");
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        NextAfter(migrations, "_AddChangeApprovalRequests").Should().EndWith("_AddGroundingAttribution", "it sorts after every earlier migration");
    }

    // Who promoted and who last edited a golden case, and who rated a query signal: three nullable ids, no key and no
    // index (recorded ids, as the linking user is), which existing rows leave empty — no backfill, so a golden case created
    // before reads as promoted by nobody. Then when a signal was promoted and to which case: two nullable columns a promotion
    // claims the signal with. Only added columns: no index, no SQL, no stored value changes.
    [TestCase(false, "mcp_eval_cases", "promoted_by_user_id", "modified_by_user_id", "mcp_query_signals", "feedback_by_user_id", "integer", "promoted_at", "timestamp with time zone", "promoted_eval_case_id")]
    [TestCase(true, "McpEvalCases", "PromotedByUserId", "ModifiedByUserId", "McpQuerySignals", "FeedbackByUserId", "int", "PromotedAt", "datetime2", "PromotedEvalCaseId")]
    public void TheGroundingAttributionMigration_AddsFiveNullableColumns_AndNothingElse_AndItsTargetModelIsTheOneBeforeTheNext(
        bool sqlServer,
        string cases,
        string promotedBy,
        string modifiedBy,
        string signals,
        string feedbackBy,
        string intType,
        string promotedAt,
        string timeType,
        string promotedCase)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var previous = MigrationNamed(context, migrations, "_AddChangeApprovalRequests");

        var migration = MigrationNamed(context, migrations, "_AddGroundingAttribution");

        migration.UpOperations.Should().OnlyContain(x => x is AddColumnOperation, "no index, no SQL: nothing rewrites a stored row");
        migration.UpOperations.OfType<AddColumnOperation>().Should().BeEquivalentTo(
            new[]
            {
                new { Schema = "beacon", Table = cases, Name = promotedBy, ColumnType = intType, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null },
                new { Schema = "beacon", Table = cases, Name = modifiedBy, ColumnType = intType, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null },
                new { Schema = "beacon", Table = signals, Name = feedbackBy, ColumnType = intType, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null },
                new { Schema = "beacon", Table = signals, Name = promotedAt, ColumnType = timeType, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null },
                new { Schema = "beacon", Table = signals, Name = promotedCase, ColumnType = intType, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null }
            },
            x => x.ExcludingMissingMembers().WithStrictOrdering());
        migration.DownOperations.Should().OnlyContain(x => x is DropColumnOperation);
        migration.DownOperations.OfType<DropColumnOperation>().Select(x => $"{x.Schema}.{x.Table}.{x.Name}")
            .Should().BeEquivalentTo(
                migration.UpOperations.OfType<AddColumnOperation>().Select(x => $"{x.Schema}.{x.Table}.{x.Name}"),
                "Down drops exactly the columns Up adds");
        var target = Initialized(migration.TargetModel!, initializer).GetRelationalModel();
        var differences = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(Initialized(previous.TargetModel!, initializer).GetRelationalModel(), target);
        differences.OfType<AddColumnOperation>().Select(x => $"{x.Table}.{x.Name} {x.ColumnType} {x.IsNullable}")
            .Should().BeEquivalentTo(
                migration.UpOperations.OfType<AddColumnOperation>().Select(x => $"{x.Table}.{x.Name} {x.ColumnType} {x.IsNullable}"),
                "the migration's Up adds exactly the columns its target model adds");
        differences.Should().HaveCount(5, "the target model differs from the one before only by this migration");
        var next = MigrationNamed(context, migrations, "_AddGroundingProvenance");
        context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(target, Initialized(next.TargetModel!, initializer).GetRelationalModel())
            .Select(x => x.GetType().Name)
            .Should().BeEquivalentTo(
                next.UpOperations.Select(x => x.GetType().Name),
                "the next migration's target model differs from its target model only by that migration");
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        NextAfter(migrations, "_AddGroundingAttribution").Should().EndWith("_AddGroundingProvenance", "it sorts after every earlier migration");
    }

    // Whether a verdict came from a curator: a boolean existing verdicts leave false (their provenance was not recorded).
    // The digests a conditional write compares — of a signal's promotable SQL and of a learned pattern's content — two
    // nullable columns existing rows leave empty (Beacon stores a row's digest in application code when it first touches
    // it). Only added columns: no index, no SQL, no stored value changes.
    [TestCase(false, "mcp_query_signals", "feedback_from_curator", "boolean", "candidate_sql_hash", "mcp_learned_patterns", "content_hash", "character varying(64)")]
    [TestCase(true, "McpQuerySignals", "FeedbackFromCurator", "bit", "CandidateSqlHash", "McpLearnedPatterns", "ContentHash", "nvarchar(64)")]
    public void TheGroundingProvenanceMigration_AddsAFalseBooleanAndTwoNullableDigests_AndNothingElse_AndItsTargetModelIsTheOneBeforeTheNext(
        bool sqlServer,
        string signals,
        string fromCurator,
        string boolType,
        string candidateHash,
        string patterns,
        string contentHash,
        string hashType)
    {
        using var provider = BuildProvider(sqlServer);
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var previous = MigrationNamed(context, migrations, "_AddGroundingAttribution");

        var migration = MigrationNamed(context, migrations, "_AddGroundingProvenance");

        migration.UpOperations.Should().OnlyContain(x => x is AddColumnOperation, "no index, no SQL: nothing rewrites a stored row");
        migration.UpOperations.OfType<AddColumnOperation>().Should().BeEquivalentTo(
            new[]
            {
                new { Schema = "beacon", Table = signals, Name = fromCurator, ColumnType = boolType, MaxLength = (int?)null, IsNullable = false, DefaultValue = (object?)false, DefaultValueSql = (string?)null },
                new { Schema = "beacon", Table = signals, Name = candidateHash, ColumnType = hashType, MaxLength = (int?)64, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null },
                new { Schema = "beacon", Table = patterns, Name = contentHash, ColumnType = hashType, MaxLength = (int?)64, IsNullable = true, DefaultValue = (object?)null, DefaultValueSql = (string?)null }
            },
            x => x.ExcludingMissingMembers().WithStrictOrdering());
        migration.DownOperations.Should().OnlyContain(x => x is DropColumnOperation);
        migration.DownOperations.OfType<DropColumnOperation>().Select(x => $"{x.Schema}.{x.Table}.{x.Name}")
            .Should().BeEquivalentTo(
                migration.UpOperations.OfType<AddColumnOperation>().Select(x => $"{x.Schema}.{x.Table}.{x.Name}"),
                "Down drops exactly the columns Up adds");
        var target = Initialized(migration.TargetModel!, initializer).GetRelationalModel();
        var differences = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(Initialized(previous.TargetModel!, initializer).GetRelationalModel(), target);
        differences.OfType<AddColumnOperation>().Select(x => $"{x.Table}.{x.Name} {x.ColumnType} {x.IsNullable} {x.DefaultValue}")
            .Should().BeEquivalentTo(
                migration.UpOperations.OfType<AddColumnOperation>().Select(x => $"{x.Table}.{x.Name} {x.ColumnType} {x.IsNullable} {x.DefaultValue}"),
                "the migration's Up adds exactly the columns its target model adds");
        differences.Should().HaveCount(3, "the target model differs from the one before only by this migration");
        var next = MigrationNamed(context, migrations, "_AddSecurityAuditTrail");
        context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(target, Initialized(next.TargetModel!, initializer).GetRelationalModel())
            .Select(x => x.GetType().Name)
            .Should().BeEquivalentTo(
                next.UpOperations.Select(x => x.GetType().Name),
                "the next migration's target model differs from its target model only by that migration");
        context.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the model");
        NextAfter(migrations, "_AddGroundingProvenance").Should().EndWith("_AddSecurityAuditTrail", "it sorts after every earlier migration");
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

    private static string IdEndingWith(IMigrationsAssembly migrations, string suffix)
    {
        return Ordered(migrations).Single(x => x.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static string NextAfter(IMigrationsAssembly migrations, string suffix)
    {
        return Ordered(migrations)
            .SkipWhile(x => !x.EndsWith(suffix, StringComparison.Ordinal))
            .Skip(1)
            .First();
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
