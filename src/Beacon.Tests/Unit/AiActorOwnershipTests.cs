using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Handlers.AiActors;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.AiActors;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// An AI actor's creator is the caller that created it, never a value from the request, and only the creator or an
/// Admin pauses, resumes, archives, refines, runs it or its subscriptions on demand, or asks for a plan revision. An
/// Admin may make another user the creator. Approving and rejecting plans are not limited to the creator yet.
/// </summary>
[TestFixture]
public class AiActorOwnershipTests
{
    private const int ActorId = 9;
    private const int PlanId = 31;
    private const int ActorSubscriptionId = 41;
    private const int PlainSubscriptionId = 42;

    private Mock<IAiActorServiceExtended> _service = null!;
    private List<AiActor> _actors = null!;
    private List<BeaconUser> _users = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new Mock<IAiActorServiceExtended>();
        _service
            .Setup(x => x.ExecuteThinkCycleAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiActorThinkResult { Success = true });
        _service
            .Setup(x => x.RefineActorAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiActorThinkResult { Success = true });
        _service
            .Setup(x => x.ApprovePlanAsync(It.IsAny<ApprovePlanOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiActorThinkResult { Success = true });
        _service
            .Setup(x => x.RequestPlanRevisionAsync(It.IsAny<RequestRevisionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AiActorPlanResult.CreateFailure("not needed"));
        _actors = [new AiActor { Id = ActorId, Name = "watcher", Instructions = "watch", DataSourceId = 1, CreatedByUserId = "ext-ana" }];
        _users =
        [
            new BeaconUser { Id = 1, ExternalId = "ext-ana", UserName = "ana", IsEnabled = true },
            new BeaconUser { Id = 2, ExternalId = "ext-ben", UserName = "ben", IsEnabled = true },
            new BeaconUser { Id = 3, ExternalId = "ext-cid", UserName = "cid", IsEnabled = false },
            new BeaconUser { Id = 5, ExternalId = "ext-admin", UserName = "admin", IsEnabled = true }
        ];
    }

    [Test]
    public async Task Create_TheCreatorIsTheSignedInCaller_NotAValueFromTheBody()
    {
        CreateAiActorOptions? created = null;
        _service
            .Setup(x => x.CreateActorAsync(It.IsAny<CreateAiActorOptions>(), It.IsAny<CancellationToken>()))
            .Callback<CreateAiActorOptions, CancellationToken>((options, _) => created = options)
            .ReturnsAsync(new AiActor { Id = ActorId, Name = "watcher", Instructions = "watch", DataSourceId = 1 });
        var command = JsonSerializer.Deserialize<CreateAiActorCommand>(
            """{"name":"watcher","instructions":"watch","dataSourceId":1,"createdByUserId":"someone-else"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var handler = new CreateAiActorHandler(_service.Object, NullLogger<CreateAiActorHandler>.Instance, Actor(Interactive("ext-ben")));

        await handler.Handle(command, CancellationToken.None);

        created.Should().NotBeNull();
        created!.CreatedByUserId.Should().Be("ext-ben");
    }

    [Test]
    public async Task Create_ByACallerWithoutAResolvableUser_IsRefused_AndNoActorIsStoredWithoutACreator()
    {
        var handler = new CreateAiActorHandler(_service.Object, NullLogger<CreateAiActorHandler>.Instance, Actor(Interactive("ext-cid")));

        var act = () => handler.Handle(new CreateAiActorCommand { Name = "watcher", Instructions = "watch", DataSourceId = 1 }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no resolvable user*");
        _service.Verify(x => x.CreateActorAsync(It.IsAny<CreateAiActorOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task EveryChangeOrRun_ByTheCreator_ReachesTheActor()
    {
        foreach (var call in ChangesAndRuns(Actor(Interactive("ext-ana"))))
        {
            await call.Should().NotThrowAsync();
        }

        _service.Verify(x => x.PauseActorAsync(ActorId, It.IsAny<CancellationToken>()), Times.Once);
        _service.Verify(x => x.ResumeActorAsync(ActorId, It.IsAny<CancellationToken>()), Times.Once);
        _service.Verify(x => x.ArchiveActorAsync(ActorId, It.IsAny<CancellationToken>()), Times.Once);
        _service.Verify(x => x.RefineActorAsync(ActorId, "more", It.IsAny<CancellationToken>()), Times.Once);
        _service.Verify(x => x.ExecuteThinkCycleAsync(ActorId, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task EveryChangeOrRun_ByAnAdminWhoIsNotTheCreator_ReachesTheActor()
    {
        foreach (var call in ChangesAndRuns(Actor(Interactive("ext-admin", RoleService.RoleNames.Admin))))
        {
            await call.Should().NotThrowAsync();
        }

        _service.Verify(x => x.ArchiveActorAsync(ActorId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase("ext-ana")]
    [TestCase(null)]
    public async Task EveryChangeOrRun_BySomeoneOtherThanTheCreator_IsForbidden(string? creator)
    {
        _actors[0].CreatedByUserId = creator;

        foreach (var call in ChangesAndRuns(Actor(Interactive("ext-ben", RoleService.RoleNames.Editor))))
        {
            await call.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*creator or an Admin*");
        }

        _service.VerifyNoOtherCalls();
    }

    [Test]
    public async Task AnOwnerlessActor_IsNotTheActorOfACallerWithoutAUser()
    {
        _actors[0].CreatedByUserId = null;

        foreach (var call in ChangesAndRuns(Actor(Interactive("ext-cid", RoleService.RoleNames.Editor))))
        {
            await call.Should().ThrowAsync<UnauthorizedAccessException>();
        }

        _service.VerifyNoOtherCalls();
    }

    [Test]
    public async Task AMissingActor_IsRefusedToAnEditorLikeAnActorThatIsNotTheirs_AndReportedAsMissingToAnAdmin()
    {
        var editor = () => AiActorOwnership.EnsureCreatorOrAdminAsync(Actor(Interactive("ext-ben", RoleService.RoleNames.Editor)), Factory(), 404, NullLogger.Instance, CancellationToken.None);
        var admin = () => AiActorOwnership.EnsureCreatorOrAdminAsync(Actor(Interactive("ext-admin", RoleService.RoleNames.Admin)), Factory(), 404, NullLogger.Instance, CancellationToken.None);

        await editor.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*creator or an Admin*");
        await admin.Should().ThrowAsync<InvalidOperationException>().WithMessage("AI actor 404 not found.");
    }

    [Test]
    public async Task Ownership_CreatorLookup_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();
        var caller = FixedActor(new BeaconActor("ext-ana", false));

        var act = () => AiActorOwnership.EnsureCreatorOrAdminAsync(caller, capture.Factory(), ActorId, NullLogger.Instance, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("created_by_user_id").And.Contain("FROM beacon.ai_actors");
        capture.CommandParameters[0].Values.Should().Contain(ActorId);
    }

    // --- plans --------------------------------------------------------------------------------------------------

    [TestCase("ext-ana", null, true)]
    [TestCase("ext-admin", RoleService.RoleNames.Admin, true)]
    [TestCase("ext-ben", RoleService.RoleNames.Editor, false)]
    public async Task RequestPlanRevision_IsTheCreatorsOrAnAdmins(string caller, string? role, bool allowed)
    {
        var handler = new RequestPlanRevisionHandler(_service.Object, NullLogger<RequestPlanRevisionHandler>.Instance, Actor(Interactive(caller, role)), Factory());

        var act = () => handler.Handle(new RequestPlanRevisionCommand { PlanId = PlanId, Feedback = "narrower" }, CancellationToken.None);

        if (allowed)
        {
            await act.Should().NotThrowAsync();
            _service.Verify(x => x.RequestPlanRevisionAsync(It.Is<RequestRevisionOptions>(y => y.PlanId == PlanId), It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            await act.Should().ThrowAsync<UnauthorizedAccessException>();
            _service.VerifyNoOtherCalls();
        }
    }

    [Test]
    public async Task RequestPlanRevision_ForAMissingPlan_IsRefusedToAnEditor()
    {
        var handler = new RequestPlanRevisionHandler(_service.Object, NullLogger<RequestPlanRevisionHandler>.Instance, Actor(Interactive("ext-ben", RoleService.RoleNames.Editor)), Factory());

        var act = () => handler.Handle(new RequestPlanRevisionCommand { PlanId = 404, Feedback = "narrower" }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _service.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ApproveAndRejectPlan_AreNotLimitedToTheCreator()
    {
        // Who may approve or reject a plan is not decided by the actor's creator: any caller with write permission can.
        var approve = new ApproveAiActorPlanHandler(_service.Object, NullLogger<ApproveAiActorPlanHandler>.Instance);
        var reject = new RejectAiActorPlanHandler(_service.Object, NullLogger<RejectAiActorPlanHandler>.Instance);

        await approve.Handle(new ApproveAiActorPlanCommand { PlanId = PlanId, UserId = "ext-ben" }, CancellationToken.None);
        await reject.Handle(new RejectAiActorPlanCommand { PlanId = PlanId, UserId = "ext-ben", Reason = "no" }, CancellationToken.None);

        _service.Verify(x => x.ApprovePlanAsync(It.Is<ApprovePlanOptions>(y => y.PlanId == PlanId), It.IsAny<CancellationToken>()), Times.Once);
        _service.Verify(x => x.RejectPlanAsync(It.Is<RejectPlanOptions>(y => y.PlanId == PlanId), It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- subscriptions an actor manages -------------------------------------------------------------------------

    [TestCase("ext-ana", null, true)]
    [TestCase("ext-admin", RoleService.RoleNames.Admin, true)]
    [TestCase("ext-ben", RoleService.RoleNames.Editor, false)]
    public async Task RunningAnActorsSubscriptionNow_IsTheCreatorsOrAnAdmins(string caller, string? role, bool allowed)
    {
        var jobs = new Mock<IJobService>();
        var handler = new TestSubscriptionHandler(jobs.Object, Factory(), Actor(Interactive(caller, role)), NullLogger<TestSubscriptionHandler>.Instance);

        var act = () => handler.Handle(new TestSubscriptionCommand(ActorSubscriptionId), CancellationToken.None);

        if (allowed)
        {
            await act.Should().NotThrowAsync();
            jobs.Verify(x => x.ExecuteQuery(ActorSubscriptionId, It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            await act.Should().ThrowAsync<UnauthorizedAccessException>();
            jobs.VerifyNoOtherCalls();
        }
    }

    [Test]
    public async Task RunningASubscriptionNoActorManages_IsUnchanged()
    {
        var jobs = new Mock<IJobService>();
        var handler = new TestSubscriptionHandler(jobs.Object, Factory(), Actor(Interactive("ext-ben", RoleService.RoleNames.Editor)), NullLogger<TestSubscriptionHandler>.Instance);

        await handler.Handle(new TestSubscriptionCommand(PlainSubscriptionId), CancellationToken.None);

        jobs.Verify(x => x.ExecuteQuery(PlainSubscriptionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- changing the creator -----------------------------------------------------------------------------------

    [Test]
    public async Task SetOwner_ByAnAdmin_MakesAnActiveUserTheCreator()
    {
        _actors[0].CreatedByUserId = null;

        await SetOwnerHandler(Interactive("ext-admin", RoleService.RoleNames.Admin)).Handle(new SetAiActorOwnerCommand(ActorId, 2), CancellationToken.None);

        _actors[0].CreatedByUserId.Should().Be("ext-ben");
    }

    [Test]
    public async Task SetOwner_ByTheCreator_IsForbidden()
    {
        var act = () => SetOwnerHandler(Interactive("ext-ana", RoleService.RoleNames.Editor)).Handle(new SetAiActorOwnerCommand(ActorId, 2), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _actors[0].CreatedByUserId.Should().Be("ext-ana");
    }

    [TestCase(3)]
    [TestCase(99)]
    public async Task SetOwner_ToSomeoneWhoIsNotAnActiveUser_IsRejected(int owner)
    {
        var act = () => SetOwnerHandler(Interactive("ext-admin", RoleService.RoleNames.Admin)).Handle(new SetAiActorOwnerCommand(ActorId, owner), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _actors[0].CreatedByUserId.Should().Be("ext-ana");
    }

    [Test]
    public async Task SetOwner_OfAMissingActor_IsReportedAsMissing()
    {
        var act = () => SetOwnerHandler(Interactive("ext-admin", RoleService.RoleNames.Admin)).Handle(new SetAiActorOwnerCommand(404, 2), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("AI actor 404 not found.");
    }

    private IEnumerable<Func<Task>> ChangesAndRuns(IBeaconActorAccessor caller)
    {
        var factory = Factory();

        yield return () => new PauseAiActorHandler(_service.Object, NullLogger<PauseAiActorHandler>.Instance, caller, factory).Handle(new PauseAiActorCommand { ActorId = ActorId }, CancellationToken.None);
        yield return () => new ResumeAiActorHandler(_service.Object, NullLogger<ResumeAiActorHandler>.Instance, caller, factory).Handle(new ResumeAiActorCommand { ActorId = ActorId }, CancellationToken.None);
        yield return () => new ArchiveAiActorHandler(_service.Object, NullLogger<ArchiveAiActorHandler>.Instance, caller, factory).Handle(new ArchiveAiActorCommand { ActorId = ActorId }, CancellationToken.None);
        yield return () => new RefineAiActorHandler(_service.Object, NullLogger<RefineAiActorHandler>.Instance, caller, factory).Handle(new RefineAiActorCommand { ActorId = ActorId, Feedback = "more" }, CancellationToken.None);
        yield return () => new ExecuteAiActorThinkCycleHandler(_service.Object, NullLogger<ExecuteAiActorThinkCycleHandler>.Instance, caller, factory).Handle(new ExecuteAiActorThinkCycleCommand { ActorId = ActorId }, CancellationToken.None);
    }

    private SetAiActorOwnerHandler SetOwnerHandler(ClaimsPrincipal caller)
    {
        return new SetAiActorOwnerHandler(Factory(), Actor(caller), NullLogger<SetAiActorOwnerHandler>.Instance);
    }

    private BeaconActorAccessor Actor(ClaimsPrincipal caller)
    {
        return new BeaconActorAccessor(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = caller } }, Factory());
    }

    private static IBeaconActorAccessor FixedActor(BeaconActor actor)
    {
        var accessor = new Mock<IBeaconActorAccessor>();
        accessor
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(actor);

        return accessor.Object;
    }

    private IDbContextFactory<BeaconContext> Factory()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(AiActor)] = RecordingBeaconContext.MemorySet(_actors, []),
                    [typeof(AiActorPlan)] = RecordingBeaconContext.MemorySet(Plans(), []),
                    [typeof(Subscription)] = RecordingBeaconContext.MemorySet(Subscriptions(), []),
                    [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(_users, [])
                },
                []));

        return factory.Object;
    }

    private List<AiActorPlan> Plans()
    {
        return [new AiActorPlan { Id = PlanId, AiActorId = ActorId, AiActor = _actors[0], Analysis = "a", ActionsJson = "[]" }];
    }

    private List<Subscription> Subscriptions()
    {
        return
        [
            new Subscription { Id = ActorSubscriptionId, QueryId = 1, CronExpression = "0 * * * *", AiActorId = ActorId, AiActor = _actors[0] },
            new Subscription { Id = PlainSubscriptionId, QueryId = 1, CronExpression = "0 * * * *" }
        ];
    }

    private static ClaimsPrincipal Interactive(string externalId, string? role = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, externalId) };
        if (role != null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
    }
}
