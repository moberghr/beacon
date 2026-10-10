using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Tasks;
using Beacon.Core.Mcp;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Tasks;

/// <summary>
/// Alert tasks are resolved, snoozed and reprioritised by their assignee or an Admin; an unassigned task is claimed by
/// assigning it to oneself; reassigning someone else's task is an Admin's; with Beacon user management the assignee
/// must be an active user; and a resolution is never overwritten. The caller is identified by its Users.ExternalId,
/// whatever credential it uses, and a task assigned by an earlier version under an id the session presents is still
/// the caller's.
/// </summary>
[TestFixture]
public class TaskWorkRulesTests
{
    private const int TaskId = 5;

    private List<QueryTask> _tasks = null!;
    private List<BeaconUser> _users = null!;
    private bool _userManagement;

    [SetUp]
    public void SetUp()
    {
        _userManagement = true;
        _tasks = [];
        _users =
        [
            User(1, "ext-ana", "ana"),
            User(2, "ext-ben", "ben"),
            User(3, "ext-cid", "cid", isEnabled: false),
            User(4, "ext-dee", "dee", archived: true),
            User(5, "ext-admin", "admin")
        ];
    }

    // --- resolve ------------------------------------------------------------------------------------------------

    [Test]
    public async Task Resolve_ByTheAssignee_RecordsTheAssigneeAsResolver()
    {
        var task = AddTask(assignee: "ext-ana");

        await ResolveHandler(Interactive("ext-ana")).Handle(new ResolveTaskCommand(TaskId, "fixed upstream"), CancellationToken.None);

        task.Resolved.Should().BeTrue();
        task.ResolvedByUserId.Should().Be("ext-ana");
        task.ResolutionNotes.Should().Be("fixed upstream");
        task.ResolvedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Test]
    public async Task Resolve_ByAnAdminWhoIsNotTheAssignee_IsAllowed()
    {
        var task = AddTask(assignee: "ext-ana");

        await ResolveHandler(Interactive("ext-admin", RoleService.RoleNames.Admin)).Handle(new ResolveTaskCommand(TaskId, null), CancellationToken.None);

        task.Resolved.Should().BeTrue();
        task.ResolvedByUserId.Should().Be("ext-admin");
    }

    [TestCase("ext-ana")]
    [TestCase(null)]
    public async Task Resolve_BySomeoneOtherThanTheAssignee_IsForbidden(string? assignee)
    {
        var task = AddTask(assignee);

        var act = () => ResolveHandler(Interactive("ext-ben", RoleService.RoleNames.Editor)).Handle(new ResolveTaskCommand(TaskId, "mine now"), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        task.Resolved.Should().BeFalse();
        task.ResolvedByUserId.Should().BeNull();
    }

    [Test]
    public async Task Resolve_AnAlreadyResolvedTask_FailsAndKeepsTheRecordedResolution()
    {
        var resolvedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var task = AddTask(assignee: "ext-ana");
        task.Resolved = true;
        task.ResolvedAt = resolvedAt;
        task.ResolvedByUserId = "ext-ana";
        task.ResolutionNotes = "original";

        var act = () => ResolveHandler(Interactive("ext-admin", RoleService.RoleNames.Admin)).Handle(new ResolveTaskCommand(TaskId, "rewritten"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already resolved*");
        task.ResolvedByUserId.Should().Be("ext-ana");
        task.ResolutionNotes.Should().Be("original");
        task.ResolvedAt.Should().Be(resolvedAt);
    }

    [Test]
    public async Task Resolve_WithTheAssigneesApiKey_RecordsTheOwnersExternalId()
    {
        var task = AddTask(assignee: "ext-ana");

        await ResolveHandler(ApiKeyOf("ana", userId: 1)).Handle(new ResolveTaskCommand(TaskId, null), CancellationToken.None);

        task.ResolvedByUserId.Should().Be("ext-ana", "an API key is its owner, named by Users.ExternalId like every other caller");
    }

    [Test]
    public async Task Resolve_ByADisabledAssignee_IsForbidden()
    {
        var task = AddTask(assignee: "ext-cid");

        var act = () => ResolveHandler(Interactive("ext-cid")).Handle(new ResolveTaskCommand(TaskId, null), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        task.Resolved.Should().BeFalse();
    }

    [Test]
    public async Task Resolve_ByAnAdminWithoutAResolvableUser_IsRefused_AndNoResolverIsLeftEmpty()
    {
        var task = AddTask(assignee: "ext-ana");
        var adminWithoutId = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, RoleService.RoleNames.Admin)], "Cookies"));

        var act = () => ResolveHandler(adminWithoutId).Handle(new ResolveTaskCommand(TaskId, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no resolvable user*");
        task.Resolved.Should().BeFalse();
    }

    [Test]
    public async Task Resolve_ATaskReassignedBetweenTheCheckAndTheWrite_IsNotResolved()
    {
        // The write is conditional on the assignee the check saw: here the row no longer matches it.
        var capture = new SqlCapture().ThenRow("ext-ana", false).ThenRowsAffected(0);
        var handler = new ResolveTaskHandler(capture.Factory(), FixedActor(new BeaconActor("ext-ana", false)), NullLogger<ResolveTaskHandler>.Instance);

        var act = () => handler.Handle(new ResolveTaskCommand(TaskId, "done"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already resolved or was reassigned meanwhile*");
    }

    [Test]
    public async Task Resolve_ConditionalWrite_Translates()
    {
        var capture = new SqlCapture().ThenRow("ext-ana", false).ThenRowsAffected(1);
        var handler = new ResolveTaskHandler(capture.Factory(), FixedActor(new BeaconActor("ext-ana", false)), NullLogger<ResolveTaskHandler>.Instance);

        await handler.Handle(new ResolveTaskCommand(TaskId, "done"), CancellationToken.None);

        capture.Commands.Should().HaveCount(2, "one read for the check, one conditional write, in the same unit of work");
        var update = capture.Commands[1];
        update.Should().StartWith("UPDATE beacon.query_tasks");
        update.Should().Contain("NOT (q.resolved)");
        update.Should().Contain("q.assignee_user_id = @");
        update.Should().Contain("resolved_by_user_id = @");
        capture.CommandParameters[1].Values.Should().Contain("ext-ana").And.Contain("done").And.Contain(TaskId);
    }

    [Test]
    public async Task TaskService_ResolvesAnOpenTask_AndNeverReResolvesOne()
    {
        var task = AddTask(assignee: "ext-ana");
        var service = new TaskService(Factory(), NullLogger<TaskService>.Instance);

        await service.ResolveTask(TaskId, "first", "ext-ana", CancellationToken.None);
        var again = () => service.ResolveTask(TaskId, "second", "ext-ben", CancellationToken.None);
        var missing = () => service.ResolveTask(404, null, "ext-ana", CancellationToken.None);

        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage($"Task {TaskId} is already resolved.");
        await missing.Should().ThrowAsync<Beacon.Core.Models.BeaconException>().WithMessage("Task 404 not found");
        task.ResolutionNotes.Should().Be("first");
        task.ResolvedByUserId.Should().Be("ext-ana");
    }

    // --- snooze and priority ------------------------------------------------------------------------------------

    [TestCase("ext-ana", null, true)]
    [TestCase("ext-admin", RoleService.RoleNames.Admin, true)]
    [TestCase("ext-ben", RoleService.RoleNames.Editor, false)]
    public async Task Snooze_IsTheAssigneesOrAnAdmins(string caller, string? role, bool allowed)
    {
        var task = AddTask(assignee: "ext-ana");
        var until = DateTime.UtcNow.AddHours(1);
        var handler = new SnoozeTaskHandler(Factory(), Actor(Interactive(caller, role)), NullLogger<SnoozeTaskHandler>.Instance);

        var act = () => handler.Handle(new SnoozeTaskCommand(TaskId, until), CancellationToken.None);

        if (allowed)
        {
            await act.Should().NotThrowAsync();
            task.SnoozedUntil.Should().Be(until);
        }
        else
        {
            await act.Should().ThrowAsync<UnauthorizedAccessException>();
            task.SnoozedUntil.Should().BeNull();
        }
    }

    [TestCase("ext-ana", null, true)]
    [TestCase("ext-admin", RoleService.RoleNames.Admin, true)]
    [TestCase("ext-ben", RoleService.RoleNames.Editor, false)]
    public async Task SetPriority_IsTheAssigneesOrAnAdmins(string caller, string? role, bool allowed)
    {
        var task = AddTask(assignee: "ext-ana");
        var handler = new SetTaskPriorityHandler(Factory(), Actor(Interactive(caller, role)), NullLogger<SetTaskPriorityHandler>.Instance);

        var act = () => handler.Handle(new SetTaskPriorityCommand(TaskId, TaskPriority.Critical), CancellationToken.None);

        if (allowed)
        {
            await act.Should().NotThrowAsync();
            task.Priority.Should().Be(TaskPriority.Critical);
        }
        else
        {
            await act.Should().ThrowAsync<UnauthorizedAccessException>();
            task.Priority.Should().Be(TaskPriority.Normal);
        }
    }

    // --- callers without a user against an unassigned task -----------------------------------------------------

    [TestCaseSource(nameof(CallersWithoutAUser))]
    public async Task EveryWorkAction_OnAnUnassignedTask_IsForbiddenToACallerWithoutAUser(ClaimsPrincipal caller)
    {
        var task = AddTask(assignee: null);
        var actor = Actor(caller);

        var calls = new Func<Task>[]
        {
            () => new ResolveTaskHandler(Factory(), actor, NullLogger<ResolveTaskHandler>.Instance).Handle(new ResolveTaskCommand(TaskId, null), CancellationToken.None),
            () => new SnoozeTaskHandler(Factory(), actor, NullLogger<SnoozeTaskHandler>.Instance).Handle(new SnoozeTaskCommand(TaskId, DateTime.UtcNow.AddHours(1)), CancellationToken.None),
            () => new SetTaskPriorityHandler(Factory(), actor, NullLogger<SetTaskPriorityHandler>.Instance).Handle(new SetTaskPriorityCommand(TaskId, TaskPriority.Critical), CancellationToken.None),
            () => AssignHandler(actor).Handle(new AssignTaskCommand(TaskId, null), CancellationToken.None)
        };

        foreach (var call in calls)
        {
            await call.Should().ThrowAsync<UnauthorizedAccessException>("a missing user id must never match a missing assignee");
        }

        task.Resolved.Should().BeFalse();
        task.SnoozedUntil.Should().BeNull();
        task.Priority.Should().Be(TaskPriority.Normal);
        task.AssigneeUserId.Should().BeNull();
    }

    // --- assign -------------------------------------------------------------------------------------------------

    [Test]
    public async Task Assign_AnUnassignedTaskToOneself_ClaimsIt()
    {
        var task = AddTask(assignee: null);

        await AssignHandler(Interactive("ext-ben", RoleService.RoleNames.Editor)).Handle(new AssignTaskCommand(TaskId, "ext-ben"), CancellationToken.None);

        task.AssigneeUserId.Should().Be("ext-ben");
    }

    [Test]
    public async Task Assign_ToTheIdTheSessionReports_ClaimsItUnderTheExternalId()
    {
        var task = AddTask(assignee: null);
        var caller = Interactive("ext-ben", RoleService.RoleNames.Editor, beaconUserId: "ben");

        await AssignHandler(caller).Handle(new AssignTaskCommand(TaskId, "ben"), CancellationToken.None);

        task.AssigneeUserId.Should().Be("ext-ben");
    }

    [Test]
    public async Task Assign_ByAnApiKeyNamingItsNumericId_ClaimsItUnderTheOwnersExternalId()
    {
        var task = AddTask(assignee: null);

        await AssignHandler(ApiKeyOf("ben", userId: 2)).Handle(new AssignTaskCommand(TaskId, "2"), CancellationToken.None);

        task.AssigneeUserId.Should().Be("ext-ben");
    }

    [Test]
    public async Task Assign_ByADisabledCallerNamingItself_IsForbidden()
    {
        var task = AddTask(assignee: null);

        var act = () => AssignHandler(Interactive("ext-cid")).Handle(new AssignTaskCommand(TaskId, "ext-cid"), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        task.AssigneeUserId.Should().BeNull();
    }

    [Test]
    public async Task Assign_AnotherUsersSessionId_IsNotTakenForTheCaller()
    {
        // "ana" is the id ana's sessions report; for ben it names someone else and is not remapped to ben.
        var task = AddTask(assignee: null);
        var caller = Interactive("ext-ben", RoleService.RoleNames.Editor, beaconUserId: "ben");

        var act = () => AssignHandler(caller).Handle(new AssignTaskCommand(TaskId, "ana"), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        task.AssigneeUserId.Should().BeNull();
    }

    [Test]
    public async Task Assign_AnUnassignedTaskToSomeoneElse_NeedsAnAdmin()
    {
        var task = AddTask(assignee: null);

        var act = () => AssignHandler(Interactive("ext-ben", RoleService.RoleNames.Editor)).Handle(new AssignTaskCommand(TaskId, "ext-ana"), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        task.AssigneeUserId.Should().BeNull();
    }

    [TestCase("ext-ben")]
    [TestCase(null)]
    [TestCase("ext-ana")]
    public async Task Assign_ATaskAssignedToSomeoneElse_NeedsAnAdmin_EvenWhenNothingWouldChange(string? newAssignee)
    {
        var task = AddTask(assignee: "ext-ana");

        var act = () => AssignHandler(Interactive("ext-ben", RoleService.RoleNames.Editor)).Handle(new AssignTaskCommand(TaskId, newAssignee), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        task.AssigneeUserId.Should().Be("ext-ana");
    }

    [Test]
    public async Task Assign_NullToAnUnassignedTask_ByAnEditor_ChangesNothing()
    {
        var task = AddTask(assignee: null);

        await AssignHandler(Interactive("ext-ben", RoleService.RoleNames.Editor)).Handle(new AssignTaskCommand(TaskId, null), CancellationToken.None);

        task.AssigneeUserId.Should().BeNull();
    }

    [Test]
    public async Task Assign_ByAnAdmin_ReassignsATaskAssignedToSomeoneElse()
    {
        var task = AddTask(assignee: "ext-ana");

        await AssignHandler(Interactive("ext-admin", RoleService.RoleNames.Admin)).Handle(new AssignTaskCommand(TaskId, "ext-ben"), CancellationToken.None);

        task.AssigneeUserId.Should().Be("ext-ben");
    }

    [TestCase("ext-ben", "ext-ben")]
    [TestCase(null, null)]
    [TestCase("  ", null)]
    [TestCase("ext-ana", "ext-ana")]
    public async Task Assign_ByTheAssignee_HandsOverReleasesOrKeepsTheTask(string? newAssignee, string? stored)
    {
        var task = AddTask(assignee: "ext-ana");

        await AssignHandler(Interactive("ext-ana")).Handle(new AssignTaskCommand(TaskId, newAssignee), CancellationToken.None);

        task.AssigneeUserId.Should().Be(stored);
    }

    [TestCase("ext-unknown")]
    [TestCase("ext-cid")]
    [TestCase("ext-dee")]
    public async Task Assign_ToAUserWhoIsNotAnActiveUser_IsRejected(string assignee)
    {
        var task = AddTask(assignee: null);

        var act = () => AssignHandler(Interactive("ext-admin", RoleService.RoleNames.Admin)).Handle(new AssignTaskCommand(TaskId, assignee), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*existing, enabled user*");
        task.AssigneeUserId.Should().BeNull();
    }

    [Test]
    public async Task Assign_ATaskReassignedBetweenTheCheckAndTheWrite_IsNotOverwritten()
    {
        var capture = new SqlCapture().ThenScalar("ext-ana").ThenRowsAffected(0);
        var handler = new AssignTaskHandler(capture.Factory(), FixedActor(new BeaconActor("ext-ana", false)), Configuration(userManagement: false), NullLogger<AssignTaskHandler>.Instance);

        var act = () => handler.Handle(new AssignTaskCommand(TaskId, null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*reassigned meanwhile*");
    }

    [Test]
    public async Task Assign_ConditionalWrite_Translates()
    {
        var capture = new SqlCapture().ThenScalar("ext-ana").ThenRowsAffected(1);
        var handler = new AssignTaskHandler(capture.Factory(), FixedActor(new BeaconActor("ext-ana", false)), Configuration(userManagement: false), NullLogger<AssignTaskHandler>.Instance);

        await handler.Handle(new AssignTaskCommand(TaskId, null), CancellationToken.None);

        capture.Commands.Should().HaveCount(2);
        var update = capture.Commands[1];
        update.Should().StartWith("UPDATE beacon.query_tasks");
        update.Should().Contain("q.assignee_user_id = @");
        capture.CommandParameters[1].Values.Should().Contain("ext-ana").And.Contain(TaskId);
    }

    [Test]
    public async Task Assign_ActiveUserLookup_Translates()
    {
        // The task (assigned to ext-ana), then the assignee check for an Admin handing it to ext-ben, who is not found.
        var capture = new SqlCapture().ThenScalar("ext-ana").ThenScalar(false);
        var handler = new AssignTaskHandler(capture.Factory(), FixedActor(new BeaconActor("ext-admin", true)), Configuration(userManagement: true), NullLogger<AssignTaskHandler>.Instance);

        var act = () => handler.Handle(new AssignTaskCommand(TaskId, "ext-ben"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*existing, enabled user*");
        capture.Commands.Should().HaveCount(2, "the write never runs for an assignee who is not an active user");
        var lookup = capture.Commands[1];
        lookup.Should().Contain("FROM beacon.users");
        lookup.Should().Contain("u.external_id = @");
        lookup.Should().Contain("u.is_enabled");
        lookup.Should().Contain("u.archived_time IS NULL");
        capture.CommandParameters[1].Values.Should().ContainSingle().Which.Should().Be("ext-ben");
    }

    // --- missing tasks and refusals -----------------------------------------------------------------------------

    [Test]
    public async Task AMissingTask_IsRefusedToAnEditorLikeATaskThatIsNotTheirs_AndReportedAsMissingToAnAdmin()
    {
        var editor = () => ResolveHandler(Interactive("ext-ben", RoleService.RoleNames.Editor)).Handle(new ResolveTaskCommand(404, null), CancellationToken.None);
        var notTheirs = () => ResolveHandler(Interactive("ext-ben", RoleService.RoleNames.Editor)).Handle(new ResolveTaskCommand(TaskId, null), CancellationToken.None);
        var admin = () => ResolveHandler(Interactive("ext-admin", RoleService.RoleNames.Admin)).Handle(new ResolveTaskCommand(404, null), CancellationToken.None);
        AddTask(assignee: "ext-ana");

        var missingForEditor = (await editor.Should().ThrowAsync<UnauthorizedAccessException>()).Which;
        var notTheirsForEditor = (await notTheirs.Should().ThrowAsync<UnauthorizedAccessException>()).Which;
        await admin.Should().ThrowAsync<InvalidOperationException>().WithMessage("Task 404 not found.");
        missingForEditor.Message.Should().Be(notTheirsForEditor.Message);
    }

    [Test]
    public async Task ARefusal_IsLoggedAsAWarningWithTheTaskAndTheCallerOnly()
    {
        AddTask(assignee: "ext-ana");
        var logs = new LogRecorder();
        var handler = new SnoozeTaskHandler(Factory(), Actor(Interactive("ext-ben", RoleService.RoleNames.Editor)), logs.For<SnoozeTaskHandler>());

        var act = () => handler.Handle(new SnoozeTaskCommand(TaskId, DateTime.UtcNow.AddHours(1)), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        var entry = logs.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain($"task {TaskId}").And.Contain("ext-ben").And.NotContain("ext-ana");
    }

    // --- tasks assigned by earlier versions ---------------------------------------------------------------------

    [Test]
    public async Task ATaskAssignedUnderTheIdTheSessionReports_IsStillTheCallersToResolve()
    {
        // Earlier versions stored the id /auth/me reports (here the user name) as the assignee.
        var task = AddTask(assignee: "ben");

        await ResolveHandler(Interactive("ext-ben", RoleService.RoleNames.Editor, beaconUserId: "ben")).Handle(new ResolveTaskCommand(TaskId, "done"), CancellationToken.None);

        task.Resolved.Should().BeTrue();
        task.ResolvedByUserId.Should().Be("ext-ben");
    }

    [Test]
    public async Task ATaskAssignedUnderAnApiKeysNumericId_IsNotTheKeyOwners()
    {
        // A numeric id is an owner id only for an API key, never a stored assignee's name for it.
        var task = AddTask(assignee: "2");

        var act = () => ResolveHandler(ApiKeyOf("ben", userId: 2)).Handle(new ResolveTaskCommand(TaskId, null), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        task.Resolved.Should().BeFalse();
    }

    // --- hosts without Beacon user management -------------------------------------------------------------------

    [Test]
    public async Task WithoutUserManagement_AnEditorClaimsAndResolvesUnderTheIdItsSessionPresents()
    {
        _userManagement = false;
        _users.Clear();
        var task = AddTask(assignee: null);
        var editor = Interactive("host-sub-7", RoleService.RoleNames.Editor, beaconUserId: "maria");

        await AssignHandler(editor).Handle(new AssignTaskCommand(TaskId, "maria"), CancellationToken.None);
        await ResolveHandler(editor).Handle(new ResolveTaskCommand(TaskId, "done"), CancellationToken.None);

        task.AssigneeUserId.Should().Be("host-sub-7");
        task.Resolved.Should().BeTrue();
        task.ResolvedByUserId.Should().Be("host-sub-7");
    }

    [Test]
    public async Task WithoutUserManagement_AnAdminAssignsToAnyId()
    {
        _userManagement = false;
        _users.Clear();
        var task = AddTask(assignee: null);

        await AssignHandler(Interactive("host-admin", RoleService.RoleNames.Admin)).Handle(new AssignTaskCommand(TaskId, "host-sub-9"), CancellationToken.None);

        task.AssigneeUserId.Should().Be("host-sub-9");
    }

    [Test]
    public async Task WithoutUserManagement_AnEditorStillCannotAssignSomeoneElse()
    {
        _userManagement = false;
        _users.Clear();
        var task = AddTask(assignee: null);

        var act = () => AssignHandler(Interactive("host-sub-7", RoleService.RoleNames.Editor)).Handle(new AssignTaskCommand(TaskId, "host-sub-9"), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        task.AssigneeUserId.Should().BeNull();
    }

    // --- the detail's caller-relative flag ----------------------------------------------------------------------

    [TestCase("ext-ana", null, true)]
    [TestCase("ana", null, true)]
    [TestCase("ext-ben", null, false)]
    [TestCase(null, null, false)]
    [TestCase("ext-ben", RoleService.RoleNames.Admin, false)]
    public async Task Detail_SaysWhetherTheCallerIsTheAssignee(string? assignee, string? role, bool assignedToCaller)
    {
        var details = new Mock<ITaskService>();
        details
            .Setup(x => x.GetTaskDetails(TaskId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details(assignee));
        var caller = Interactive("ext-ana", role, beaconUserId: "ana");
        var userContext = new HttpContextUserContext(Accessor(caller));
        var handler = new GetTaskDetailHandler(details.Object, userContext, Actor(caller));

        var result = await handler.Handle(new GetTaskDetailQuery(TaskId), CancellationToken.None);

        result!.AssignedToCaller.Should().Be(assignedToCaller);
        result.AssigneeUserId.Should().Be(assignee);
    }

    [Test]
    public async Task Detail_DoesNotReadTheNotificationsStoredResults_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();

        var details = await new TaskService(capture.Factory(), NullLogger<TaskService>.Instance).GetTaskDetails(TaskId, null, CancellationToken.None);

        details.Should().BeNull();
        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("FROM beacon.notifications");
        sql.Should().NotContain(".results", "a task's detail carries no stored result rows");
    }

    private static IEnumerable<TestCaseData> CallersWithoutAUser()
    {
        yield return new TestCaseData(Interactive("ext-cid", RoleService.RoleNames.Editor)).SetName("{m}(disabled user)");
        yield return new TestCaseData(Interactive("ext-dee", RoleService.RoleNames.Editor)).SetName("{m}(archived user)");
        yield return new TestCaseData(ApiKeyOf("ghost", userId: 99)).SetName("{m}(API key of an unknown owner)");
        yield return new TestCaseData(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, RoleService.RoleNames.Editor)], "Cookies")))
            .SetName("{m}(session without an id)");
    }

    private ResolveTaskHandler ResolveHandler(ClaimsPrincipal caller)
    {
        return new ResolveTaskHandler(Factory(), Actor(caller), NullLogger<ResolveTaskHandler>.Instance);
    }

    private AssignTaskHandler AssignHandler(ClaimsPrincipal caller)
    {
        return AssignHandler(Actor(caller));
    }

    private AssignTaskHandler AssignHandler(IBeaconActorAccessor actor)
    {
        return new AssignTaskHandler(Factory(), actor, Configuration(_userManagement), NullLogger<AssignTaskHandler>.Instance);
    }

    private BeaconActorAccessor Actor(ClaimsPrincipal caller)
    {
        return new BeaconActorAccessor(Accessor(caller), Factory());
    }

    private static IBeaconActorAccessor FixedActor(BeaconActor actor)
    {
        var accessor = new Mock<IBeaconActorAccessor>();
        accessor
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(actor);

        return accessor.Object;
    }

    private static BeaconConfiguration Configuration(bool userManagement)
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = userManagement;

        return configuration;
    }

    private QueryTask AddTask(string? assignee)
    {
        var task = new QueryTask
        {
            Id = TaskId,
            SubscriptionId = 1,
            LatestResultCount = 3,
            AssigneeUserId = assignee
        };
        _tasks.Add(task);

        return task;
    }

    private static Beacon.Core.DTOs.TaskDetailsData Details(string? assignee)
    {
        return new Beacon.Core.DTOs.TaskDetailsData
        {
            Id = TaskId,
            Subscription = new Beacon.Core.DTOs.SubscriptionSummary(1, "Report", null),
            LatestResultCount = 3,
            NotificationCount = 0,
            Notifications = [],
            CreatedAt = DateTime.UtcNow,
            Resolved = false,
            QueryId = 1,
            QueryName = "Report",
            AssigneeUserId = assignee
        };
    }

    private IDbContextFactory<BeaconContext> Factory()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(QueryTask)] = RecordingBeaconContext.MemorySet(_tasks, []),
                    [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(_users, [])
                },
                []));

        return factory.Object;
    }

    private static BeaconUser User(int id, string externalId, string userName, bool isEnabled = true, bool archived = false)
    {
        return new BeaconUser
        {
            Id = id,
            ExternalId = externalId,
            UserName = userName,
            IsEnabled = isEnabled,
            ArchivedTime = archived ? DateTime.UtcNow : null
        };
    }

    private static HttpContextAccessor Accessor(ClaimsPrincipal user)
    {
        return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } };
    }

    private static ClaimsPrincipal Interactive(string externalId, string? role = null, string? beaconUserId = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, externalId) };
        if (role != null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (beaconUserId != null)
        {
            claims.Add(new Claim(BeaconClaims.UserId, beaconUserId));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
    }

    // The principal ApiKeyAuthMiddleware produces: the numeric user id in NameIdentifier, the owner's user name claim.
    private static ClaimsPrincipal ApiKeyOf(string userName, int userId)
    {
        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod),
            new(McpCallerClaimTypes.Scope, "Execute"),
            new(McpCallerClaimTypes.UserNameClaim, userName)
        ];

        return new ClaimsPrincipal(new ClaimsIdentity(claims, McpCallerClaimTypes.ApiKeyAuthenticationType));
    }
}
