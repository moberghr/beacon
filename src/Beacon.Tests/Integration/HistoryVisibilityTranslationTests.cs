using Beacon.Connector.PostgreSql;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Integration;

/// <summary>
/// The history lists are records of what ran: a run stays listed after its subscription, query or migration job is
/// archived, while the project-scope predicate of the restricted read is unchanged. Statements are captured, never run
/// (§4.7).
/// </summary>
[TestFixture]
[NonParallelizable] // DbConnectionFactory is a process-wide registry.
public class HistoryVisibilityTranslationTests
{
    private static readonly StoredRunScope RestrictedScope = StoredRunScope.From(new ProjectSet([1], false, []));
    private static readonly ProjectSet RestrictedSet = new([1], false, []) { IsConfinedToList = true };

    [TestCase(false)]
    [TestCase(true)]
    public async Task NotificationList_Unrestricted_DoesNotFilterArchivedRows_Translates(bool sqlServer)
    {
        var capture = Capture(sqlServer).ThenScalar(0);

        await NotificationsOn(capture).GetQueryExecutionHistory(
            new GetQueryExecutionHistoryRequest { Scope = StoredRunScope.Unrestricted },
            CancellationToken.None);

        capture.Commands.Should().ContainSingle()
            .Which.Should().NotContain(sqlServer ? "[ArchivedTime] IS NULL" : "archived_time IS NULL");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task NotificationList_Restricted_KeepsTheProjectPredicateAndListsArchivedRows_Translates(bool sqlServer)
    {
        var capture = Capture(sqlServer).ThenScalar(0);

        await NotificationsOn(capture).GetQueryExecutionHistory(
            new GetQueryExecutionHistoryRequest { Scope = RestrictedScope },
            CancellationToken.None);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain(ProjectLinks(sqlServer));
        // The only archive test left is on the legacy path (a run recorded without data sources), which stays an Admin's.
        var archivedChecks = sql.Split(sqlServer ? "[ArchivedTime] IS NULL" : "archived_time IS NULL").Length - 1;
        archivedChecks.Should().Be(2, "only the legacy-run subscription and query checks remain");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MigrationExecutions_DoNotFilterArchivedJobs_Translates(bool sqlServer)
    {
        var capture = Capture(sqlServer).ThenScalar(0);

        await MigrationsOn(capture).GetMigrationExecutions(new GetMigrationExecutionsRequest(), CancellationToken.None);

        capture.Commands.Should().ContainSingle()
            .Which.Should().NotContain(sqlServer ? "[ArchivedTime] IS NULL" : "archived_time IS NULL");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MigrationExecutions_Restricted_KeepsTheSourcePredicate_Translates(bool sqlServer)
    {
        var capture = Capture(sqlServer).ThenScalar(0);

        await MigrationsOn(capture).GetMigrationExecutions(
            new GetMigrationExecutionsRequest { Readable = RestrictedSet },
            CancellationToken.None);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain(ProjectLinks(sqlServer));
        sql.Should().NotContain(sqlServer ? "[ArchivedTime] IS NULL" : "archived_time IS NULL");
    }

    [Test]
    public void CapErrorMessage_LongText_FitsTheColumn()
    {
        var capped = MigrationService.CapErrorMessage(new string('x', 9000));

        capped.Should().HaveLength(MigrationService.ErrorMessageMaxLength);
        capped.Should().HaveLength(4000);
    }

    [Test]
    public void CapErrorMessage_ShortOrMissingText_IsUnchanged()
    {
        MigrationService.CapErrorMessage(new string('x', 4000)).Should().HaveLength(4000);
        MigrationService.CapErrorMessage("failed").Should().Be("failed");
        MigrationService.CapErrorMessage(null).Should().BeNull();
    }

    private static string ProjectLinks(bool sqlServer)
    {
        return sqlServer ? "[ProjectDataSources]" : "project_data_sources";
    }

    private static SqlCapture Capture(bool sqlServer)
    {
        return sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture();
    }

    private static NotificationService NotificationsOn(SqlCapture capture)
    {
        return new NotificationService(
            capture.Factory(),
            adapterFactory: null!,
            secretProtector: null!,
            destinationPolicy: null!,
            Options.Create(new NotificationChannelOptions()),
            TestChangeApprovals.Deliveries(),
            NullLogger<NotificationService>.Instance);
    }

    private static MigrationService MigrationsOn(SqlCapture capture)
    {
        var encryption = new Mock<IEncryptionService>();

        return new MigrationService(
            capture.Factory(),
            TestTransport.Resolver(encryption.Object),
            Mock.Of<IQueryService>(),
            Mock.Of<IQueryExecutionPreviewService>(),
            Mock.Of<IBeaconUserContext>(),
            MigrationTestWriters.All(),
            NullLogger<MigrationService>.Instance,
            Options.Create(new MigrationOptions { Enabled = true }),
            TestChangeApprovals.Submitter(), TestProjectAccess.Actors(), new RecordingSecurityAudit(), TimeProvider.System);
    }
}
