using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.AI.Handlers.AiActors;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.AiActors;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;

namespace Beacon.Tests.Unit;

/// <summary>
/// An AI actor's creator is the caller that created it, never a value from the request, and only the creator or an
/// Admin pauses, resumes, archives, refines, runs it or its subscriptions on demand, or approves, rejects or asks for a
/// revision of one of its plans. An Admin may make another user the creator.
/// </summary>
[TestFixture]
public class AiActorOwnershipTests
{
    private const int ActorId = 9;
    private const int PlanId = 31;
    private const int ActorSubscriptionId = 41;
    private const int PlainSubscriptionId = 42;

    private TestAiActorService _service = null!;
    private List<AiActor> _actors = null!;
    private List<BeaconUser> _users = null!;
    private string? _revisionRequester;
    private AiActorPlan? _viewedPlan;
    private List<AiActorStatus> _statusesAfter = null!;

    [SetUp]
    public void SetUp()
    {
        _revisionRequester = null;
        _viewedPlan = null;
        _statusesAfter = [];
        _actors = [new AiActor { Id = ActorId, Name = "watcher", Instructions = "watch", DataSourceId = 1, CreatedByUserId = "ext-ana" }];
        _users =
        [
            new BeaconUser { Id = 1, ExternalId = "ext-ana", UserName = "ana", IsEnabled = true },
            new BeaconUser { Id = 2, ExternalId = "ext-ben", UserName = "ben", IsEnabled = true },
            new BeaconUser { Id = 3, ExternalId = "ext-cid", UserName = "cid", IsEnabled = false },
            new BeaconUser { Id = 5, ExternalId = "ext-admin", UserName = "admin", IsEnabled = true },
            new BeaconUser { Id = 6, ExternalId = "ext-ula", UserName = "ula", IsEnabled = true }
        ];

        // The service reads the plan the plan view shows, or the plans (each context its own copy, as each reads the
        // database afresh), through contexts of its own: whether a handler reached it is told apart from the handler's reads.
        _service = new TestAiActorService(_actors, () => _viewedPlan != null ? [_viewedPlan] : Plans());
        _service.DataSources.Add(new DataSource { Id = 1, Name = "warehouse", DataSourceType = DataSourceType.Database, DatabaseEngineType = DatabaseEngineType.PostgreSQL, MetadataLoadingEnabled = false, EncryptedConnectionData = string.Empty });
    }

    [Test]
    public async Task Create_TheCreatorIsTheSignedInCaller_NotAValueFromTheBody()
    {
        var command = JsonSerializer.Deserialize<CreateAiActorCommand>(
            """{"name":"watcher","instructions":"watch","dataSourceId":1,"createdByUserId":"someone-else"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var handler = new CreateAiActorHandler(_service.Service, NullLogger<CreateAiActorHandler>.Instance, Actor(Interactive("ext-ben")), new BeaconConfiguration());

        await handler.Handle(command, CancellationToken.None);

        var created = _service.Added.OfType<AiActor>().Should().ContainSingle().Subject;
        created.CreatedByUserId.Should().Be("ext-ben");
    }

    // Whether the actor's changes wait for approval comes from the body, and is on when the body does not say; only an
    // Admin turns it off.
    [TestCase("", "ext-ben", null, true)]
    [TestCase(",\"requiresApproval\":true", "ext-ben", null, true)]
    [TestCase(",\"requiresApproval\":false", "ext-admin", RoleService.RoleNames.Admin, false)]
    public async Task Create_RequiresApproval_RoundTripsThroughTheHandler(string field, string caller, string? role, bool expected)
    {
        var command = JsonSerializer.Deserialize<CreateAiActorCommand>(
            $$"""{"name":"watcher","instructions":"watch","dataSourceId":1{{field}}}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var handler = new CreateAiActorHandler(_service.Service, NullLogger<CreateAiActorHandler>.Instance, Actor(Interactive(caller, role)), new BeaconConfiguration());

        var result = await handler.Handle(command, CancellationToken.None);

        var created = _service.Added.OfType<AiActor>().Should().ContainSingle().Subject;
        created.RequiresApproval.Should().Be(expected);
        result.RequiresApproval.Should().Be(expected);
    }

    // An actor whose changes do not wait for approval runs what the model decides: a caller who is not an Admin (here the
    // project writer creating it) cannot create one, and nothing is created.
    [Test]
    public async Task Create_WithApprovalOff_ByACallerWhoIsNotAnAdmin_IsForbidden()
    {
        var command = new CreateAiActorCommand { Name = "watcher", Instructions = "watch", DataSourceId = 1, RequiresApproval = false };
        var handler = new CreateAiActorHandler(_service.Service, NullLogger<CreateAiActorHandler>.Instance, Actor(Interactive("ext-ben")), new BeaconConfiguration());

        var act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(CreateAiActorHandler.ApprovalOffRefusal);
        AssertServiceNotReached();
    }

    [Test]
    public async Task Create_ByACallerWithoutAResolvableUser_IsRefused_AndNoActorIsStoredWithoutACreator()
    {
        var handler = new CreateAiActorHandler(_service.Service, NullLogger<CreateAiActorHandler>.Instance, Actor(Interactive("ext-cid")), new BeaconConfiguration());

        var act = () => handler.Handle(new CreateAiActorCommand { Name = "watcher", Instructions = "watch", DataSourceId = 1 }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(ScheduledWorkOwner.OwnerRequiredMessage);
        AssertServiceNotReached();
    }

    [Test]
    public async Task EveryChangeOrRun_ByTheCreator_ReachesTheActor()
    {
        foreach (var call in ChangesAndRuns(Actor(Interactive("ext-ana"))))
        {
            await call.Should().NotThrowAsync();
        }

        // Paused, resumed and archived in turn, refined with the feedback, then one think cycle asked for on demand.
        _statusesAfter.Take(3).Should().Equal(AiActorStatus.Paused, AiActorStatus.Active, AiActorStatus.Archived);
        AssertArchivedOnce();
        _service.LlmRequests.Select(x => x.Operation).Should().Equal(LlmOperations.ActorRefine, LlmOperations.ActorThink);
        _service.Added.OfType<AiActorConversation>().Should().ContainSingle(x => x.Role == ConversationRole.User)
            .Which.Should().BeEquivalentTo(new { AiActorId = ActorId, MessageContent = "more" }, x => x.ExcludingMissingMembers());
        _service.Added.OfType<AiActorExecution>().Should().OnlyContain(x => x.AiActorId == ActorId && x.TriggeringSubscriptionId == null);
        _actors[0].ThinkCount.Should().Be(1);
    }

    [Test]
    public async Task EveryChangeOrRun_ByAnAdminWhoIsNotTheCreator_ReachesTheActor()
    {
        foreach (var call in ChangesAndRuns(Actor(Interactive("ext-admin", RoleService.RoleNames.Admin))))
        {
            await call.Should().NotThrowAsync();
        }

        _statusesAfter[2].Should().Be(AiActorStatus.Archived);
        AssertArchivedOnce();
    }

    [TestCase("ext-ana")]
    [TestCase(null)]
    public async Task EveryChangeOrRun_BySomeoneOtherThanTheCreator_IsForbidden(string? creator)
    {
        _actors[0].CreatedByUserId = creator;

        foreach (var call in ChangesAndRuns(Actor(Interactive("ext-ben", RoleService.RoleNames.Editor))))
        {
            await call.Should().ThrowAsync<BeaconForbiddenException>().WithMessage("*creator or an Admin*");
        }

        AssertServiceNotReached();
    }

    [Test]
    public async Task AnOwnerlessActor_IsNotTheActorOfACallerWithoutAUser()
    {
        _actors[0].CreatedByUserId = null;

        foreach (var call in ChangesAndRuns(Actor(Interactive("ext-cid", RoleService.RoleNames.Editor))))
        {
            await call.Should().ThrowAsync<BeaconForbiddenException>();
        }

        AssertServiceNotReached();
    }

    [Test]
    public async Task AMissingActor_IsRefusedToAnEditorLikeAnActorThatIsNotTheirs_AndReportedAsMissingToAnAdmin()
    {
        var editor = () => AiActorOwnership.EnsureCreatorOrAdminAsync(Actor(Interactive("ext-ben", RoleService.RoleNames.Editor)), Factory(), 404, NullLogger.Instance, CancellationToken.None);
        var admin = () => AiActorOwnership.EnsureCreatorOrAdminAsync(Actor(Interactive("ext-admin", RoleService.RoleNames.Admin)), Factory(), 404, NullLogger.Instance, CancellationToken.None);

        await editor.Should().ThrowAsync<BeaconForbiddenException>().WithMessage("*creator or an Admin*");
        await admin.Should().ThrowAsync<InvalidOperationException>().WithMessage("AI actor 404 not found.");
    }

    [Test]
    public async Task Ownership_CreatorLookup_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();
        var caller = FixedActor(new BeaconActor("ext-ana", false));

        var act = () => AiActorOwnership.EnsureCreatorOrAdminAsync(caller, capture.Factory(), ActorId, NullLogger.Instance, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
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
        var handler = new RequestPlanRevisionHandler(_service.Service, NullLogger<RequestPlanRevisionHandler>.Instance, Actor(Interactive(caller, role)), Factory());

        var act = () => handler.Handle(new RequestPlanRevisionCommand { PlanId = PlanId, Feedback = "narrower" }, CancellationToken.None);

        if (allowed)
        {
            await act.Should().NotThrowAsync();
            AssertDecided(AiActorPlanStatus.RevisionRequested, caller);
        }
        else
        {
            await act.Should().ThrowAsync<BeaconForbiddenException>();
            AssertServiceNotReached();
        }
    }

    [Test]
    public async Task RequestPlanRevision_ForAMissingPlan_IsRefusedToAnEditor()
    {
        var handler = new RequestPlanRevisionHandler(_service.Service, NullLogger<RequestPlanRevisionHandler>.Instance, Actor(Interactive("ext-ben", RoleService.RoleNames.Editor)), Factory());

        var act = () => handler.Handle(new RequestPlanRevisionCommand { PlanId = 404, Feedback = "narrower" }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        AssertServiceNotReached();
    }

    [TestCase("ext-ana", null, true)]
    [TestCase("ext-admin", RoleService.RoleNames.Admin, true)]
    [TestCase("ext-ben", RoleService.RoleNames.Editor, false)]
    public async Task ApprovePlan_IsTheCreatorsOrAnAdmins(string caller, string? role, bool allowed)
    {
        var act = () => ApproveHandler(Interactive(caller, role)).Handle(new ApproveAiActorPlanCommand { PlanId = PlanId, UserId = caller }, CancellationToken.None);

        if (allowed)
        {
            await act.Should().NotThrowAsync();
            AssertDecided(AiActorPlanStatus.Executed, caller);
        }
        else
        {
            await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage("*creator or an Admin*");
            AssertServiceNotReached();
        }
    }

    // With the approval workflow on, approving a plan (which executes its actions) takes a second person: an Approver (an
    // Admin holds the permission) other than the actor's creator.
    [TestCase("ext-ben", "Approver", true)]
    [TestCase("ext-admin", RoleService.RoleNames.Admin, true)]
    [TestCase("ext-ana", "Approver", false)]
    [TestCase("ext-ana", null, false)]
    [TestCase("ext-ben", RoleService.RoleNames.Editor, false)]
    public async Task ApprovePlan_WithTheWorkflowOn_IsAnApproversOtherThanTheCreator(string caller, string? role, bool allowed)
    {
        var workflowOn = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true } };

        var act = () => ApproveHandler(Interactive(caller, role), workflowOn).Handle(new ApproveAiActorPlanCommand { PlanId = PlanId, UserId = caller }, CancellationToken.None);

        if (allowed)
        {
            await act.Should().NotThrowAsync();
            AssertDecided(AiActorPlanStatus.Executed, caller);
        }
        else
        {
            await act.Should().ThrowAsync<BeaconForbiddenException>();
            AssertServiceNotReached();
        }
    }

    // Whoever asked for a revision of the plan shaped it with their feedback: with the workflow on they do not approve it
    // (nor, through the plan's chain, the query changes it makes); another Approver does.
    [TestCase("ext-ben", false)]
    [TestCase("ext-admin", true)]
    public async Task ApprovePlan_WithTheWorkflowOn_IsNotTheRevisionRequestersInItsChain(string caller, bool allowed)
    {
        _revisionRequester = "ext-ben";
        var workflowOn = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true } };
        var role = caller == "ext-admin" ? RoleService.RoleNames.Admin : "Approver";

        var act = () => ApproveHandler(Interactive(caller, role), workflowOn).Handle(new ApproveAiActorPlanCommand { PlanId = PlanId, UserId = caller }, CancellationToken.None);

        if (allowed)
        {
            await act.Should().NotThrowAsync();
            AssertDecided(AiActorPlanStatus.Executed, caller);
            return;
        }

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        AssertServiceNotReached();
    }

    // The creator is the creator however they present themselves: a creator recorded by an earlier version as an API key's
    // numeric owner id, or as a session's own id, does not approve the actor's plan by signing in another way.
    [TestCase("1")]
    [TestCase("session-ana")]
    [TestCase("EXT-ANA")]
    public async Task ApprovePlan_WithTheWorkflowOn_ByTheCreatorUnderAnotherOfTheirIds_IsRefused(string recordedCreator)
    {
        _actors[0].CreatedByUserId = recordedCreator;
        var creator = new BeaconActor("ext-ana", false) { Kind = BeaconPrincipalKind.User, BeaconUserId = 1, IsActiveUser = true, SessionIds = ["session-ana"] };

        var act = () => PlanApproval(FixedActor(creator)).Handle(new ApproveAiActorPlanCommand { PlanId = PlanId }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        AssertServiceNotReached();
    }

    // A caller Beacon cannot name could be the creator: holding the Approver permission does not let it approve a plan.
    [Test]
    public async Task ApprovePlan_WithTheWorkflowOn_ByACallerWithoutAUserId_IsRefused()
    {
        var act = () => PlanApproval(FixedActor(BeaconActor.Nobody)).Handle(new ApproveAiActorPlanCommand { PlanId = PlanId }, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        AssertServiceNotReached();
    }

    [Test]
    public async Task ApprovePlan_ByARevisionRequester_WhenTheHostAllowsSelfApproval_Proceeds()
    {
        _revisionRequester = "ext-ben";
        var selfApproval = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true, AllowSelfApproval = true } };

        var act = () => ApproveHandler(Interactive("ext-ben", "Approver"), selfApproval).Handle(new ApproveAiActorPlanCommand { PlanId = PlanId, UserId = "ext-ben" }, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // A plan records its reviewer as the caller Beacon names, never an id the request carries: that is the id the query
    // changes it makes compare their approvers with.
    [Test]
    public async Task DecidingAPlan_RecordsTheCallerAsBeaconNamesThem_NotTheIdTheRequestCarries()
    {
        await ApproveHandler(Interactive("ext-ana")).Handle(new ApproveAiActorPlanCommand { PlanId = PlanId, UserId = "someone-else" }, CancellationToken.None);
        await RejectHandler(Interactive("ext-ana")).Handle(new RejectAiActorPlanCommand { PlanId = PlanId, UserId = "someone-else", Reason = "no" }, CancellationToken.None);
        await new RequestPlanRevisionHandler(_service.Service, NullLogger<RequestPlanRevisionHandler>.Instance, Actor(Interactive("ext-ana")), Factory())
            .Handle(new RequestPlanRevisionCommand { PlanId = PlanId, UserId = "someone-else", Feedback = "narrower" }, CancellationToken.None);

        AssertDecided(AiActorPlanStatus.Executed, "ext-ana");
        AssertDecided(AiActorPlanStatus.Rejected, "ext-ana");
        AssertDecided(AiActorPlanStatus.RevisionRequested, "ext-ana");
    }

    [Test]
    public async Task ApprovePlan_WithTheWorkflowOnAndSelfApprovalAllowed_IsTheCreatorsWhoIsAnApprover()
    {
        var selfApproval = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true, AllowSelfApproval = true } };

        var act = () => ApproveHandler(Interactive("ext-ana", "Approver"), selfApproval).Handle(new ApproveAiActorPlanCommand { PlanId = PlanId, UserId = "ext-ana" }, CancellationToken.None);

        await act.Should().NotThrowAsync();
        AssertDecided(AiActorPlanStatus.Executed, "ext-ana");
    }

    [TestCase("ext-ana", null, true)]
    [TestCase("ext-admin", RoleService.RoleNames.Admin, true)]
    [TestCase("ext-ben", RoleService.RoleNames.Editor, false)]
    public async Task RejectPlan_IsTheCreatorsOrAnAdmins(string caller, string? role, bool allowed)
    {
        var act = () => RejectHandler(Interactive(caller, role)).Handle(new RejectAiActorPlanCommand { PlanId = PlanId, UserId = caller, Reason = "no" }, CancellationToken.None);

        if (allowed)
        {
            await act.Should().NotThrowAsync();
            AssertDecided(AiActorPlanStatus.Rejected, caller);
        }
        else
        {
            await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage("*creator or an Admin*");
            AssertServiceNotReached();
        }
    }

    [Test]
    public async Task DecidingAPlanOfAnActorWithoutACreator_IsAnAdminsOnly()
    {
        _actors[0].CreatedByUserId = null;

        var approve = () => ApproveHandler(Interactive("ext-ana", RoleService.RoleNames.Editor)).Handle(new ApproveAiActorPlanCommand { PlanId = PlanId, UserId = "ext-ana" }, CancellationToken.None);
        var reject = () => RejectHandler(Interactive("ext-ana", RoleService.RoleNames.Editor)).Handle(new RejectAiActorPlanCommand { PlanId = PlanId, UserId = "ext-ana", Reason = "no" }, CancellationToken.None);
        var adminApprove = () => ApproveHandler(Interactive("ext-admin", RoleService.RoleNames.Admin)).Handle(new ApproveAiActorPlanCommand { PlanId = PlanId, UserId = "ext-admin" }, CancellationToken.None);

        await approve.Should().ThrowAsync<BeaconForbiddenException>();
        await reject.Should().ThrowAsync<BeaconForbiddenException>();
        await adminApprove.Should().NotThrowAsync();
        AssertDecided(AiActorPlanStatus.Executed, "ext-admin");
        _service.ServedPlans.Should().NotContain(x => x.Status == AiActorPlanStatus.Rejected);
    }

    [Test]
    public async Task DecidingAMissingPlan_IsRefusedToAnEditor_AndReportedAsMissingToAnAdmin()
    {
        var editorApprove = () => ApproveHandler(Interactive("ext-ben", RoleService.RoleNames.Editor)).Handle(new ApproveAiActorPlanCommand { PlanId = 404, UserId = "ext-ben" }, CancellationToken.None);
        var editorReject = () => RejectHandler(Interactive("ext-ben", RoleService.RoleNames.Editor)).Handle(new RejectAiActorPlanCommand { PlanId = 404, UserId = "ext-ben", Reason = "no" }, CancellationToken.None);
        var adminApprove = () => ApproveHandler(Interactive("ext-admin", RoleService.RoleNames.Admin)).Handle(new ApproveAiActorPlanCommand { PlanId = 404, UserId = "ext-admin" }, CancellationToken.None);

        await editorApprove.Should().ThrowAsync<BeaconForbiddenException>().WithMessage("*creator or an Admin*");
        await editorReject.Should().ThrowAsync<BeaconForbiddenException>().WithMessage("*creator or an Admin*");
        await adminApprove.Should().ThrowAsync<InvalidOperationException>().WithMessage("AI actor plan 404 not found.");
        AssertServiceNotReached();
    }

    [Test]
    public async Task PlanOwnership_CreatorLookup_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();
        var caller = FixedActor(new BeaconActor("ext-ana", false));

        var act = () => AiActorOwnership.EnsureCreatorOrAdminOfPlanAsync(caller, capture.Factory(), PlanId, NullLogger.Instance, CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("FROM beacon.ai_actor_plans").And.Contain("beacon.ai_actors").And.Contain("created_by_user_id");
        capture.CommandParameters[0].Values.Should().Contain(PlanId);
    }

    // --- subscriptions an actor manages -------------------------------------------------------------------------

    [TestCase("ext-ana", null, true)]
    [TestCase("ext-admin", RoleService.RoleNames.Admin, true)]
    [TestCase("ext-ben", RoleService.RoleNames.Editor, false)]
    public async Task RunningAnActorsSubscriptionNow_IsTheCreatorsOrAnAdmins(string caller, string? role, bool allowed)
    {
        var jobs = new Mock<IJobService>();
        var handler = new TestSubscriptionHandler(jobs.Object, Factory(), Actor(Interactive(caller, role)), TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted), NullLogger<TestSubscriptionHandler>.Instance);

        var act = () => handler.Handle(new TestSubscriptionCommand(ActorSubscriptionId), CancellationToken.None);

        if (allowed)
        {
            await act.Should().NotThrowAsync();
            jobs.Verify(x => x.ExecuteQuery(ActorSubscriptionId, It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            await act.Should().ThrowAsync<BeaconForbiddenException>();
            jobs.VerifyNoOtherCalls();
        }
    }

    [Test]
    public async Task RunningASubscriptionNoActorManages_IsUnchanged()
    {
        var jobs = new Mock<IJobService>();
        var handler = new TestSubscriptionHandler(jobs.Object, Factory(), Actor(Interactive("ext-ben", RoleService.RoleNames.Editor)), TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted), NullLogger<TestSubscriptionHandler>.Instance);

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

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        _actors[0].CreatedByUserId.Should().Be("ext-ana");
    }

    [Test]
    public async Task SetOwner_ByAUserAdmin_MakesAnotherUserTheCreator_ButNeverThemself()
    {
        var userAdmin = Interactive("ext-ula", BeaconPermissions.UserAdmin);

        var self = () => SetOwnerHandler(userAdmin).Handle(new SetAiActorOwnerCommand(ActorId, 6), CancellationToken.None);
        await self.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(OwnerTransfer.SelfMessage);
        _actors[0].CreatedByUserId.Should().Be("ext-ana");

        await SetOwnerHandler(userAdmin).Handle(new SetAiActorOwnerCommand(ActorId, 2), CancellationToken.None);

        _actors[0].CreatedByUserId.Should().Be("ext-ben");
    }

    [TestCase(BeaconPermissions.Auditor)]
    [TestCase(BeaconPermissions.SecurityAdmin)]
    public async Task SetOwner_ByAHolderOfAnotherPermission_IsForbidden(string permission)
    {
        var act = () => SetOwnerHandler(Interactive("ext-ula", permission)).Handle(new SetAiActorOwnerCommand(ActorId, 2), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage("Only an Admin or a UserAdmin*");
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

        await act.Should().ThrowAsync<BeaconNotFoundException>().WithMessage("AI actor 404 not found.");
    }

    // What the plan view tells a reviewer matches what approving does: the same cases as the approval handler's.
    [TestCase("ext-ben", "Approver", true, false)]
    [TestCase("ext-admin", RoleService.RoleNames.Admin, true, true)]
    [TestCase("ext-ana", "Approver", false, true)]
    [TestCase("ext-ana", null, false, true)]
    [TestCase("ext-ben", RoleService.RoleNames.Editor, false, false)]
    public async Task PlanView_WithTheWorkflowOn_SaysWhetherTheCallerMayApprove(string caller, string? role, bool mayApprove, bool mayRejectOrRevise)
    {
        var workflowOn = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true } };

        var result = await PlanView(Interactive(caller, role), workflowOn).Handle(new GetAiActorPlanQuery { PlanId = PlanId }, CancellationToken.None);

        result!.MayApprove.Should().Be(mayApprove);
        result.MayRejectOrRevise.Should().Be(mayRejectOrRevise);
        if (mayApprove)
        {
            result.ApproveBlockedReason.Should().BeNull();
        }
        else
        {
            result.ApproveBlockedReason.Should().NotBeNullOrWhiteSpace();
        }
    }

    [TestCase("ext-ana", null, true)]
    [TestCase("ext-admin", RoleService.RoleNames.Admin, true)]
    [TestCase("ext-ben", "Approver", false)]
    public async Task PlanView_WithTheWorkflowOff_LetsTheCreatorOrAnAdminApprove(string caller, string? role, bool allowed)
    {
        var result = await PlanView(Interactive(caller, role)).Handle(new GetAiActorPlanQuery { PlanId = PlanId }, CancellationToken.None);

        result!.MayApprove.Should().Be(allowed);
        result.MayRejectOrRevise.Should().Be(allowed);
        result.ApproveBlockedReason.Should().Be(allowed ? null : "Only the AI actor's creator or an Admin can change or run it.");
    }

    [Test]
    public async Task PlanView_ForACallerWhoMayNotWriteTheActorsDataSource_OffersNoDecision()
    {
        var result = await PlanView(Interactive("ext-ana"), scope: ProjectAccessScope.None).Handle(new GetAiActorPlanQuery { PlanId = PlanId }, CancellationToken.None);

        result!.MayApprove.Should().BeFalse();
        result.MayRejectOrRevise.Should().BeFalse();
        result.ApproveBlockedReason.Should().Contain("write access");
    }

    [Test]
    public async Task PlanView_ForAPlanNoLongerPending_OffersNoDecisionAndNoReason()
    {
        _actors[0].CreatedByUserId = "ext-ana";
        var decided = new AiActorPlan { Id = PlanId, AiActorId = ActorId, AiActor = _actors[0], Status = AiActorPlanStatus.Rejected, Analysis = "a", ActionsJson = "[]", ProposedAt = DateTime.UtcNow };

        var result = await PlanView(Interactive("ext-ana"), plan: decided).Handle(new GetAiActorPlanQuery { PlanId = PlanId }, CancellationToken.None);

        result!.MayApprove.Should().BeFalse();
        result.MayRejectOrRevise.Should().BeFalse();
        result.ApproveBlockedReason.Should().BeNull();
    }

    // Each change or run starts from a status the service accepts it from and records the status it leaves the actor in;
    // the refinement and the cycle are of an actor whose changes run as decided, so the pending plan does not hold them.
    private IEnumerable<Func<Task>> ChangesAndRuns(IBeaconActorAccessor caller)
    {
        var factory = Factory();

        yield return () => Run(AiActorStatus.Active, () => new PauseAiActorHandler(_service.Service, NullLogger<PauseAiActorHandler>.Instance, caller, factory).Handle(new PauseAiActorCommand { ActorId = ActorId }, CancellationToken.None));
        yield return () => Run(AiActorStatus.Paused, () => new ResumeAiActorHandler(_service.Service, NullLogger<ResumeAiActorHandler>.Instance, caller, factory).Handle(new ResumeAiActorCommand { ActorId = ActorId }, CancellationToken.None));
        yield return () => Run(AiActorStatus.Active, () => new ArchiveAiActorHandler(_service.Service, NullLogger<ArchiveAiActorHandler>.Instance, caller, factory).Handle(new ArchiveAiActorCommand { ActorId = ActorId }, CancellationToken.None));
        yield return () => Run(AiActorStatus.Active, () => new RefineAiActorHandler(_service.Service, NullLogger<RefineAiActorHandler>.Instance, caller, factory).Handle(new RefineAiActorCommand { ActorId = ActorId, Feedback = "more" }, CancellationToken.None), false);
        yield return () => Run(AiActorStatus.Active, () => new ExecuteAiActorThinkCycleHandler(_service.Service, NullLogger<ExecuteAiActorThinkCycleHandler>.Instance, caller, factory).Handle(new ExecuteAiActorThinkCycleCommand { ActorId = ActorId }, CancellationToken.None), false);
    }

    private async Task Run(AiActorStatus from, Func<Task> call, bool requiresApproval = true)
    {
        _actors[0].Status = from;
        _actors[0].RequiresApproval = requiresApproval;

        await call();

        _statusesAfter.Add(_actors[0].Status);
    }

    private ApproveAiActorPlanHandler ApproveHandler(ClaimsPrincipal caller, BeaconConfiguration? configuration = null)
    {
        return new ApproveAiActorPlanHandler(
            _service.Service,
            NullLogger<ApproveAiActorPlanHandler>.Instance,
            Actor(caller),
            TestPermissions.ForPrincipal(caller),
            Factory(),
            configuration ?? new BeaconConfiguration());
    }

    // Plan approval with the workflow on, by a caller holding the Approver permission.
    private ApproveAiActorPlanHandler PlanApproval(IBeaconActorAccessor caller)
    {
        return new ApproveAiActorPlanHandler(
            _service.Service,
            NullLogger<ApproveAiActorPlanHandler>.Instance,
            caller,
            TestPermissions.Holding(BeaconPermissions.Approver),
            Factory(),
            new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true } });
    }

    // The plan view of the pending plan (or of <paramref name="plan"/>) for a caller with <paramref name="scope"/> (default: everything).
    private GetAiActorPlanHandler PlanView(ClaimsPrincipal caller, BeaconConfiguration? configuration = null, ProjectAccessScope? scope = null, AiActorPlan? plan = null)
    {
        _viewedPlan = plan;

        return new GetAiActorPlanHandler(
            _service.Service,
            Mock.Of<IOptionsMonitor<AiActorOptions>>(x => x.CurrentValue == new AiActorOptions()),
            TimeProvider.System,
            Actor(caller),
            TestPermissions.ForPrincipal(caller),
            TestProjectAccess.Evaluator(scope ?? TestProjectAccess.Unrestricted),
            Factory(),
            configuration ?? new BeaconConfiguration());
    }

    private RejectAiActorPlanHandler RejectHandler(ClaimsPrincipal caller)
    {
        return new RejectAiActorPlanHandler(_service.Service, NullLogger<RejectAiActorPlanHandler>.Instance, Actor(caller), Factory());
    }

    private SetAiActorOwnerHandler SetOwnerHandler(ClaimsPrincipal caller)
    {
        return new SetAiActorOwnerHandler(Factory(), Actor(caller), TestPermissions.ForPrincipal(caller), TestProjectAccess.Authorizer(TestProjectAccess.Unrestricted, actors: Actor(caller)), new RecordingSecurityAudit(), NullLogger<SetAiActorOwnerHandler>.Instance);
    }

    // The handler never reached the service: it opened no context and asked the model nothing.
    private void AssertServiceNotReached()
    {
        _service.ContextsOpened.Should().Be(0);
        _service.LlmRequests.Should().BeEmpty();
    }

    // The service decided the plan once, recording the caller as Beacon names them as its reviewer.
    private void AssertDecided(AiActorPlanStatus status, string reviewer)
    {
        _service.ServedPlans
            .Where(x => x.Id == PlanId)
            .Where(x => x.Status == status)
            .Should().ContainSingle()
            .Which.ReviewedByUserId.Should().Be(reviewer);
    }

    private void AssertArchivedOnce()
    {
        _service.Audit.Of(SecurityAuditActions.AiActorArchived).Should().ContainSingle().Which.ResourceId.Should().Be(ActorId.ToString());
    }

    private BeaconActorAccessor Actor(ClaimsPrincipal caller)
    {
        return TestProjectAccess.ActorAccessor(TestRequestPrincipal.For(caller), Factory());
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

    // The plan, and, when a revision requester is set, the parent plan they sent back for revision.
    private List<AiActorPlan> Plans()
    {
        if (_revisionRequester == null)
        {
            return [new AiActorPlan { Id = PlanId, AiActorId = ActorId, AiActor = _actors[0], Analysis = "a", ActionsJson = "[]" }];
        }

        return
        [
            new AiActorPlan { Id = PlanId - 1, AiActorId = ActorId, AiActor = _actors[0], Status = AiActorPlanStatus.RevisionRequested, ReviewedByUserId = _revisionRequester, Analysis = "a", ActionsJson = "[]" },
            new AiActorPlan { Id = PlanId, AiActorId = ActorId, AiActor = _actors[0], ParentPlanId = PlanId - 1, Analysis = "a", ActionsJson = "[]" }
        ];
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
