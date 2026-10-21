using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.AiActors;
using Beacon.Core.Handlers.McpAudit;
using Beacon.Core.Handlers.Queries;
using Beacon.Core.Handlers.QueryFolders;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// Every action of the catalogue has a place in Beacon that records it, and the security-relevant changes that record
/// one do: a query's lock and its tool exposure (both also attributed to the caller), a query moved between folders
/// (attributed), a read of the MCP audit; and an owner
/// change is made only from the owner it read, so the event never names a stale previous owner.
/// </summary>
[TestFixture]
public class SecurityAuditCoverageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    // Actions defined for an event site a later change adds: the row-security management endpoints record these.
    private static readonly string[] Reserved =
    [
        nameof(SecurityAuditActions.AttributeDefined),
        nameof(SecurityAuditActions.AttributeUpdated),
        nameof(SecurityAuditActions.AttributeArchived),
        nameof(SecurityAuditActions.AttributeValuesSet),
        nameof(SecurityAuditActions.RowPolicyCreated),
        nameof(SecurityAuditActions.RowPolicyUpdated),
        nameof(SecurityAuditActions.RowPolicyArchived),
        nameof(SecurityAuditActions.TableClassified)
    ];

    [Test]
    public void EveryCatalogueAction_IsRecordedSomewhere()
    {
        var root = RepositoryRoot();
        var sources = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}Beacon.Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(x => !x.EndsWith("SecurityAuditActions.cs", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();
        var actions = typeof(SecurityAuditActions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.IsLiteral)
            .Select(x => x.Name)
            .ToList();

        var unrecorded = actions
            .Where(x => !sources.Any(y => Regex.IsMatch(y, $@"\bSecurityAuditActions\.{x}\b")))
            .Except(Reserved)
            .ToList();

        var recordedReserved = Reserved
            .Where(x => sources.Any(y => Regex.IsMatch(y, $@"\bSecurityAuditActions\.{x}\b")))
            .ToList();

        actions.Should().HaveCountGreaterThan(90);
        unrecorded.Should().BeEmpty("an action in the catalogue that nothing records promises an event the trail never holds");
        recordedReserved.Should().BeEmpty("an action something records is no longer reserved");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ChangingAQuerysLock_IsRecorded_AndAttributedToTheCaller(bool locking)
    {
        var capture = new SqlCapture().ThenRow(!locking, locking ? DBNull.Value : "ext-ada").ThenRowsAffected(1);
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, "ext-ada"));
        var handler = LockHandler(capture, audit, TestProjectAccess.Actors(3, "ext-ada"));

        var result = await handler.Handle(new ToggleQueryLockCommand { QueryId = 5, Lock = locking, UserId = "ext-ada" }, CancellationToken.None);

        result.IsLocked.Should().Be(locking);
        var changed = audit.Single("loan book", "ext-ada", "ext-ben");
        changed.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.QueryLockChanged, ResourceType = SecurityAuditResources.Query, ResourceId = "5", ActorUserId = (int?)3 },
            x => x.ExcludingMissingMembers());
        changed.DetailsJson.Should().Be($"{{\"locked\":{locking.ToString().ToLowerInvariant()}}}");
        var update = capture.Commands[1];
        update.Should().StartWith("UPDATE beacon.queries");
        update.Should().Contain("modified_by_user_id").And.Contain("modified_time");
        capture.CommandParameters[1].Values.Should().Contain("ext-ada").And.Contain(Now.UtcDateTime);
    }

    [TestCase("ext-ben")]
    [TestCase("3")]
    [TestCase("EXT-ADA")]
    public async Task UnlockingAQueryAnotherUserLocked_IsRefused_AndChangesNothing(string holder)
    {
        var capture = new SqlCapture().ThenRow(true, holder);
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, "ext-ada"));
        var handler = LockHandler(capture, audit, TestProjectAccess.Actors(3, "ext-ada"));

        var act = () => handler.Handle(new ToggleQueryLockCommand { QueryId = 5, Lock = false, UserId = "ext-ada" }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        capture.Commands.Should().HaveCount(1, "nothing is written");
        audit.Events.Should().BeEmpty();
    }

    [TestCase("ext-ben", true)]
    [TestCase(null, false)]
    public async Task UnlockingAQuery_IsAllowed_ForAnAdminOrWhenNoHolderIsRecorded(string? holder, bool asAdmin)
    {
        var capture = new SqlCapture().ThenRow(true, holder ?? (object)DBNull.Value).ThenRowsAffected(1);
        var actor = new BeaconActor("ext-ada", asAdmin) { Kind = BeaconPrincipalKind.User, BeaconUserId = 3, IsActiveUser = true, RoleLevel = asAdmin ? 3 : 2 };
        var actors = new Mock<IBeaconActorAccessor>();
        actors.Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(actor);

        var result = await LockHandler(capture, new RecordingSecurityAudit(RecordingSecurityAudit.User(3, "ext-ada")), actors.Object)
            .Handle(new ToggleQueryLockCommand { QueryId = 5, Lock = false, UserId = "ext-ada" }, CancellationToken.None);

        result.IsLocked.Should().BeFalse();
    }

    // Another unlock and a relock by someone else landed between this request's read and its write: the write finds the
    // lock no longer in the observed state, changes nothing and records nothing, and the newer lock stands.
    [TestCase(true)]
    [TestCase(false)]
    public async Task ALockChange_ThatLostARace_IsAConflict_AndRecordsNothing(bool locking)
    {
        var capture = new SqlCapture().ThenRow(!locking, locking ? DBNull.Value : "ext-ada").ThenRowsAffected(0);
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, "ext-ada"));
        var handler = LockHandler(capture, audit, TestProjectAccess.Actors(3, "ext-ada"));

        var act = () => handler.Handle(new ToggleQueryLockCommand { QueryId = 5, Lock = locking, UserId = "ext-ada" }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>();
        audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task UnlockingAQuery_WritesOnlyWhileTheObservedLockStands_Translates()
    {
        var capture = new SqlCapture().ThenRow(true, "ext-ada").ThenRowsAffected(1);
        await LockHandler(capture, new RecordingSecurityAudit(RecordingSecurityAudit.User(3, "ext-ada")), TestProjectAccess.Actors(3, "ext-ada"))
            .Handle(new ToggleQueryLockCommand { QueryId = 5, Lock = false, UserId = "ext-ada" }, CancellationToken.None);

        var update = capture.Commands[1];
        update.Should().Contain("is_locked").And.Contain("locked_by_user_id = @");
        capture.CommandParameters[1].Values.Should().Contain("ext-ada").And.Contain(5);

        var holderless = new SqlCapture().ThenRow(true, DBNull.Value).ThenRowsAffected(1);
        await LockHandler(holderless, new RecordingSecurityAudit(RecordingSecurityAudit.User(3, "ext-ada")), TestProjectAccess.Actors(3, "ext-ada"))
            .Handle(new ToggleQueryLockCommand { QueryId = 5, Lock = false, UserId = "ext-ada" }, CancellationToken.None);

        holderless.Commands[1].Should().Contain("locked_by_user_id IS NULL");
    }

    [Test]
    public async Task LockingAQuery_WritesOnlyWhileItIsUnlocked_Translates()
    {
        var capture = new SqlCapture().ThenRow(false, DBNull.Value).ThenRowsAffected(1);
        await LockHandler(capture, new RecordingSecurityAudit(RecordingSecurityAudit.User(3, "ext-ada")), TestProjectAccess.Actors(3, "ext-ada"))
            .Handle(new ToggleQueryLockCommand { QueryId = 5, Lock = true, UserId = "ext-ada" }, CancellationToken.None);

        capture.Commands[1].Should().Contain("NOT (").And.Contain("is_locked");
    }

    private ToggleQueryLockHandler LockHandler(SqlCapture capture, ISecurityAuditRecorder audit, IBeaconActorAccessor actors)
    {
        return new ToggleQueryLockHandler(capture.Factory(), actors, audit, new FakeTimeProvider(Now), NullLogger<ToggleQueryLockHandler>.Instance);
    }

    [Test]
    public async Task MovingAQueryBetweenFolders_IsAttributedToTheCaller()
    {
        var query = new Query { Id = 5, Name = "loan book" };
        var folders = Set([new QueryFolder { Id = 2, Name = "finance", Path = "/finance" }]);
        var handler = new MoveQueryToFolderHandler(
            Factory(new Dictionary<Type, object> { [typeof(Query)] = Set([query]), [typeof(QueryFolder)] = folders }),
            TestProjectAccess.Actors(3, "ext-ada"),
            new FakeTimeProvider(Now));

        await handler.Handle(new MoveQueryToFolderCommand(5, 2), CancellationToken.None);

        query.Should().BeEquivalentTo(new { FolderId = (int?)2, ModifiedByUserId = "ext-ada", ModifiedTime = (DateTime?)Now.UtcDateTime }, x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task DeletingAFolder_ThatMovesItsQueriesToItsParent_AttributesEachMovedQueryToTheCaller()
    {
        var moved = new Query { Id = 5, Name = "loan book", FolderId = 2 };
        var folder = new QueryFolder { Id = 2, Name = "finance", Path = "/finance", ParentFolderId = 1, Queries = [moved] };
        var handler = new DeleteQueryFolderHandler(
            Factory(new Dictionary<Type, object> { [typeof(QueryFolder)] = Set([folder]), [typeof(Query)] = Set([moved]) }),
            TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted),
            TestProjectAccess.Actors(3, "ext-ada"),
            new FakeTimeProvider(Now),
            NullLogger<DeleteQueryFolderHandler>.Instance);

        await handler.Handle(new DeleteQueryFolderCommand(2, MoveQueriesToParent: true), CancellationToken.None);

        moved.Should().BeEquivalentTo(new { FolderId = (int?)1, ModifiedByUserId = "ext-ada", ModifiedTime = (DateTime?)Now.UtcDateTime }, x => x.ExcludingMissingMembers());
        folder.ArchivedTime.Should().NotBeNull();
    }

    // The owner changed (or the actor was archived) between the read of the owner and the change: the change touches
    // nothing, refuses with 409, and records nothing that would name the owner read as the previous one.
    [TestCase(false, "a.created_by_user_id = @")]
    [TestCase(true, "[a].[CreatedByUserId] = @")]
    public async Task AnOwnerChange_IsMadeOnlyFromTheOwnerItRead_Translates(bool sqlServer, string ownerPredicate)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture())
            .ThenScalar("ext-ben")
            .ThenRow("ext-ana")
            .ThenRowsAffected(0);
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, "ext-admin", isAdmin: true));
        var actors = RecordingSecurityAudit.Accessor(RecordingSecurityAudit.User(3, "ext-admin", isAdmin: true));
        var handler = new SetAiActorOwnerHandler(
            capture.Factory(),
            actors,
            TestPermissions.Admin,
            TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted, actors: actors),
            audit,
            NullLogger<SetAiActorOwnerHandler>.Instance);

        var act = () => handler.Handle(new SetAiActorOwnerCommand(9, 8), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconConflictException>().WithMessage(SetAiActorOwnerHandler.OwnerChangedMessage);
        capture.Commands[2].Should().StartWith("UPDATE").And.Contain(ownerPredicate);
        capture.CommandParameters[2].Values.Should().Contain("ext-ana", "the owner the handler read is the one the update requires");
        audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task ReadingTheMcpAudit_IsRecordedFirst_WithTheFiltersShapeOnly()
    {
        var capture = new SqlCapture().AnsweringCountsWithOne();
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(9, "ext-auditor"));
        var handler = new GetMcpAuditLogsHandler(capture.Factory(), NullLoggerFactory.Instance, SecurityAuditTestKit.Reads(capture.Factory(), audit));
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        await handler.Handle(new GetMcpAuditLogsQuery(from, from.AddDays(7), 2, "ask", "caller-hash-value", 5, 1, 50, 9), CancellationToken.None);

        var read = audit.Single("caller-hash-value", "\"ask\"");
        read.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.McpAuditRead, ResourceType = SecurityAuditResources.McpAudit, ProjectId = (int?)2, ActorUserId = (int?)9 },
            x => x.ExcludingMissingMembers());
        read.DetailsJson.Should().Contain("\"hasToolFilter\":true").And.Contain("\"hasCallerHashFilter\":true").And.Contain("\"userId\":5");
    }

    private static DbSet<T> Set<T>(List<T> rows) where T : class
    {
        return RecordingBeaconContext.MemorySet(rows, []);
    }

    private static IDbContextFactory<BeaconContext> Factory(Dictionary<Type, object> sets)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, []));

        return factory.Object;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Beacon.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests run inside the repository");

        return directory!.FullName;
    }
}
