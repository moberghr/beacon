using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Handlers.DataMigration;
using Beacon.Core.Handlers.Users;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using Beacon.Core.HostData;

namespace Beacon.Tests.Integration;

/// <summary>
/// The queries behind functional permissions translate on both providers (no database; captured SQL or
/// <c>ToQueryString()</c> only): a stored user's role names, a user administration target's roles, the links a
/// migration's reach is decided by, the migration catalogue and run history filtered by the data sources the caller reads,
/// and the subscriptions and data contracts a recipient is attached to, filtered the same way.
/// </summary>
[TestFixture]
public class FunctionalPermissionTranslationTests
{
    private static readonly ProjectSet Readable = new([1, 2], true, [30]);

    [Test]
    public async Task StoredRoleNames_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();
        await StoredRoleNamesAsync(capture);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("FROM beacon.user_roles").And.Contain("INNER JOIN beacon.roles").And.Contain("user_id = @");
        sql.Should().Contain("is_system_role").And.Contain("level = 0", "only functional roles grant a permission");
    }

    [Test]
    public async Task StoredRoleNames_SqlServer_Translates()
    {
        var capture = SqlCapture.OnSqlServer().ThenNoRows();
        await StoredRoleNamesAsync(capture);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("FROM [beacon].[UserRoles]").And.Contain("INNER JOIN [beacon].[Roles]").And.Contain("[UserId] = @");
        sql.Should().Contain("[IsSystemRole]").And.Contain("[Level] = 0", "only functional roles grant a permission");
    }

    [Test]
    public async Task UserAdministrationTarget_ReadsItsRolesInOneCommand_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();
        var rules = new UserAdministrationRules(
            capture.Factory(),
            TestProjectAccess.Actors(3, "ext-ana"),
            TestPermissions.Holding(BeaconPermissions.UserAdmin),
            Options.Create(new RoleMappingOptions()),
            NullLogger<UserAdministrationRules>.Instance);

        await rules.DemandMayChangeUserAsync(8, CancellationToken.None);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("is_super_admin").And.Contain("user_roles").And.Contain("roles");
        sql.Should().Contain("is_system_role").And.Contain("level", "every functional role is protected, whatever its name");
        sql.Should().NotContain("archived_time IS NULL", "archived users are read too");
    }

    [Test]
    public async Task UserAdministrationTarget_ReadsItsRolesInOneCommand_SqlServer_Translates()
    {
        var capture = SqlCapture.OnSqlServer().ThenNoRows();
        var rules = new UserAdministrationRules(
            capture.Factory(),
            TestProjectAccess.Actors(3, "ext-ana"),
            TestPermissions.Holding(BeaconPermissions.UserAdmin),
            Options.Create(new RoleMappingOptions()),
            NullLogger<UserAdministrationRules>.Instance);

        await rules.DemandMayChangeUserAsync(8, CancellationToken.None);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("[IsSuperAdmin]").And.Contain("[UserRoles]").And.Contain("[Roles]");
        sql.Should().Contain("[IsSystemRole]").And.Contain("[Level]");
        sql.Should().NotContain("[ArchivedTime] IS NULL", "archived users are read too");
    }

    [Test]
    public async Task UsersWithConflictingStoredRoles_ReadInOneCommand_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();

        var count = await SeparationOfDutiesReport.CountUsersWithConflictsAsync(capture.Factory(), new RoleMappingOptions(), CancellationToken.None);

        count.Should().Be(0);
        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("FROM beacon.user_roles").And.Contain("is_system_role").And.Contain("is_enabled").And.Contain("is_super_admin");
    }

    [Test]
    public void MigrationStepSources_FilteredByReadableProjects_Translates()
    {
        using var context = NpgsqlTestContext.Create();
        var ids = new List<int> { 10, 20 };

        var sql = context.DataSources
            .Where(x => ids.Contains(x.Id))
            .WhereSourceAccessible(context, Readable, x => x.Id)
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().Contain("FROM beacon.data_sources").And.Contain("project_data_sources");
    }

    [Test]
    public async Task MigrationReach_ReadsLinksWithTheirProjectsAndTheUnlinkedRule_Translates()
    {
        var capture = new SqlCapture().ThenNoRows().ThenNoRows();
        var access = new MigrationJobAccess(
            TestProjectAccess.Evaluator(TestProjectAccess.FastPath),
            TestProjectAccess.Authorizer(TestProjectAccess.FastPath),
            capture.Factory(),
            TestProjectAccess.Actors(),
            Options.Create(new ProjectAuthorizationOptions()),
            NullLogger<MigrationJobAccess>.Instance);

        await access.DemandAsync(10, "SELECT 1", 20, CancellationToken.None);

        capture.Commands.Should().HaveCount(2);
        capture.Commands[0].Should().Contain("FROM beacon.project_data_sources").And.Contain("INNER JOIN beacon.projects")
            .And.Contain("requires_membership").And.Contain("archived_time");
        capture.Commands[1].Should().Contain("FROM beacon.data_sources").And.Contain("open_when_unlinked");
    }

    [Test]
    public async Task MigrationReach_ReadsLinksWithTheirProjectsAndTheUnlinkedRule_SqlServer_Translates()
    {
        var capture = SqlCapture.OnSqlServer().ThenNoRows().ThenNoRows();
        var access = new MigrationJobAccess(
            TestProjectAccess.Evaluator(TestProjectAccess.FastPath),
            TestProjectAccess.Authorizer(TestProjectAccess.FastPath),
            capture.Factory(),
            TestProjectAccess.Actors(),
            Options.Create(new ProjectAuthorizationOptions()),
            NullLogger<MigrationJobAccess>.Instance);

        await access.DemandAsync(10, "SELECT 1", 20, CancellationToken.None);

        capture.Commands.Should().HaveCount(2);
        capture.Commands[0].Should().Contain("FROM [beacon].[ProjectDataSources]").And.Contain("INNER JOIN [beacon].[Projects]")
            .And.Contain("[RequiresMembership]").And.Contain("[ArchivedTime]");
        capture.Commands[1].Should().Contain("FROM [beacon].[DataSources]").And.Contain("[OpenWhenUnlinked]");
    }

    [Test]
    public void MigrationCatalogue_FilteredByReadableSources_Translates()
    {
        using var context = NpgsqlTestContext.Create();

        var sql = context.MigrationJobs
            .WhereSourceAccessible(context, Readable, x => x.DataSourceId)
            .WhereSourceAccessible(context, Readable, x => x.DestinationDataSourceId)
            .ToQueryString();

        sql.Should().Contain("data_source_id").And.Contain("destination_data_source_id");
        sql.Split("project_data_sources").Length.Should().Be(3, "each end of the job is checked against the readable projects");
    }

    [Test]
    public void MigrationCatalogue_FilteredByReadableSources_SqlServer_Translates()
    {
        using var context = SqlServerTestContext.Create();

        var sql = context.MigrationJobs
            .WhereSourceAccessible(context, Readable, x => x.DataSourceId)
            .WhereSourceAccessible(context, Readable, x => x.DestinationDataSourceId)
            .ToQueryString();

        sql.Should().Contain("[DestinationDataSourceId]").And.Contain("[ProjectDataSources]");
    }

    [Test]
    public void MigrationRuns_FilteredThroughTheirJob_Translates()
    {
        using var context = NpgsqlTestContext.Create();

        var sql = context.MigrationExecutions
            .WhereSourceAccessible(context, Readable, x => x.MigrationJob.DataSourceId)
            .WhereSourceAccessible(context, Readable, x => x.MigrationJob.DestinationDataSourceId)
            .ToQueryString();

        sql.Should().Contain("migration_jobs").And.Contain("destination_data_source_id").And.Contain("project_data_sources");
    }

    [TestCase(false, "destination_data_source_id", "project_data_sources")]
    [TestCase(true, "[DestinationDataSourceId]", "[ProjectDataSources]")]
    public async Task MigrationRunHistory_FiltersBothEndsOfTheJob_Translates(bool sqlServer, string destinationColumn, string linksTable)
    {
        var filtered = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).AnsweringCountsWithOne();
        var unfiltered = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).AnsweringCountsWithOne();

        await Migrations(filtered).GetMigrationExecutions(new GetMigrationExecutionsRequest { Readable = Readable }, CancellationToken.None);
        await Migrations(unfiltered).GetMigrationExecutions(new GetMigrationExecutionsRequest(), CancellationToken.None);

        filtered.Commands.Should().NotBeEmpty();
        filtered.Commands.Should().AllSatisfy(x =>
        {
            x.Should().Contain(destinationColumn);
            x.Split(linksTable).Length.Should().Be(3, "the job's source and its destination are each checked against the readable projects");
        });
        unfiltered.Commands.Should().NotBeEmpty().And.AllSatisfy(x => x.Should().NotContain(linksTable, "without a readable set every run is listed"));
    }

    [Test]
    public void RecipientAttachments_FilteredByReadableSubscriptions_SqlServer_Translates()
    {
        using var context = SqlServerTestContext.Create();

        var sql = context.Subscriptions
            .Where(x => x.Recipients.Any(y => y.Id == 5))
            .WhereAllSourcesInOneProject(context, Readable, x => x.Query.Steps.Select(y => y.DataSourceId), x => x.Query.OwnerUserId)
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().Contain("Recipient").And.Contain("[QuerySteps]").And.Contain("[ProjectDataSources]");
    }

    [Test]
    public void RecipientAttachments_FilteredByReadableDataContracts_Translates()
    {
        using var context = NpgsqlTestContext.Create();

        var sql = context.DataContracts
            .Where(x => x.Recipients.Any(y => y.Id == 5))
            .WhereSourceAccessible(context, Readable, x => x.DataSourceId)
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().Contain("recipient").And.Contain("data_source_id").And.Contain("project_data_sources");
    }

    [Test]
    public void RecipientAttachments_FilteredByReadableDataContracts_SqlServer_Translates()
    {
        using var context = SqlServerTestContext.Create();

        var sql = context.DataContracts
            .Where(x => x.Recipients.Any(y => y.Id == 5))
            .WhereSourceAccessible(context, Readable, x => x.DataSourceId)
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().Contain("Recipient").And.Contain("[DataSourceId]").And.Contain("[ProjectDataSources]");
    }

    [Test]
    public void RecipientAttachments_FilteredByReadableSubscriptions_Translates()
    {
        using var context = NpgsqlTestContext.Create();

        var sql = context.Subscriptions
            .Where(x => x.Recipients.Any(y => y.Id == 5))
            .WhereAllSourcesInOneProject(context, Readable, x => x.Query.Steps.Select(y => y.DataSourceId), x => x.Query.OwnerUserId)
            .Select(x => x.Id)
            .ToQueryString();

        sql.Should().Contain("recipient").And.Contain("query_steps").And.Contain("project_data_sources");
    }

    private static async Task StoredRoleNamesAsync(SqlCapture capture)
    {
        var actor = new BeaconActor("ext-ana", false) { Kind = BeaconPrincipalKind.User, BeaconUserId = 3, IsActiveUser = true };
        var accessor = new BeaconPermissionAccessor(
            Mock.Of<IBeaconActorAccessor>(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()) == Task.FromResult(actor)),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity("Cookies")) } },
            capture.Factory(),
            new BeaconConfiguration(),
            Options.Create(new RoleMappingOptions()),
            TimeProvider.System,
            new BeaconPermissionLog(TimeProvider.System, NullLogger<BeaconPermissionLog>.Instance));

        var permissions = await accessor.GetCurrentAsync(CancellationToken.None);

        permissions.Should().Be(BeaconPermissionSet.None);
    }

    private static MigrationService Migrations(SqlCapture capture)
    {
        return new MigrationService(
            capture.Factory(),
            Mock.Of<IDataSourceConnectionResolver>(),
            Mock.Of<IQueryService>(),
            Mock.Of<IQueryExecutionPreviewService>(),
            Mock.Of<IBeaconUserContext>(),
            new MigrationBulkContextFactory([]),
            NullLogger<MigrationService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new Beacon.Core.Configuration.MigrationOptions()),
            TestChangeApprovals.Submitter(), TestProjectAccess.Actors(), new RecordingSecurityAudit(), TimeProvider.System);
    }
}
