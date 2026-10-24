using System.Security.Claims;
using Beacon.AI.Handlers.AiActors;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.AiActors;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// An AI actor's think cycle run on demand as a background task: the start is the creator's or an Admin's, is on record
/// with its task and answers with the ticket; the task runs the actor's own think cycle as the user who asked, re-checks
/// that user's right to run it, and maps a cycle that did not run to a skipped task with a code.
/// </summary>
[TestFixture]
public class AiActorThinkTaskTests
{
    private const int ActorId = 9;

    private static readonly BackgroundTaskTicket Ticket = new(31, "job-1", BackgroundTaskState.Queued);

    private Mock<IAiActorServiceExtended> _service = null!;
    private List<AiActor> _actors = null!;
    private List<BeaconUser> _users = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new Mock<IAiActorServiceExtended>();
        _service
            .Setup(x => x.ExecuteThinkCycleAsync(ActorId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiActorThinkResult { Success = true });
        _actors = [new AiActor { Id = ActorId, Name = "watcher", Instructions = "watch", DataSourceId = 1, CreatedByUserId = "ext-ana", Status = AiActorStatus.Active }];
        _users =
        [
            new BeaconUser { Id = 1, ExternalId = "ext-ana", UserName = "ana", IsEnabled = true },
            new BeaconUser { Id = 2, ExternalId = "ext-ben", UserName = "ben", IsEnabled = true },
            new BeaconUser { Id = 5, ExternalId = "ext-admin", UserName = "admin", IsEnabled = true }
        ];
    }

    [Test]
    public async Task Start_ByTheCreator_HandsTheCycleToABackgroundTask_AndIsOnRecordWithItsTask()
    {
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(1, "ext-ana"));
        var tasks = Tasks(Ticket, audit);

        var ticket = await StartHandler(tasks.Object, Interactive("ext-ana")).Handle(new StartAiActorThinkCommand { ActorId = ActorId }, CancellationToken.None);

        ticket.Should().Be(Ticket);
        _service.Verify(x => x.ExecuteThinkCycleAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never, "the task thinks, not the request");
        var started = audit.Single("ext-ana");
        started.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.AiActorThinkStarted, ResourceType = SecurityAuditResources.AiActor, ResourceId = "9" },
            x => x.ExcludingMissingMembers());
        started.DetailsJson.Should().Be("{\"taskId\":31}");
    }

    [Test]
    public async Task Start_ByAnAdmin_IsAccepted()
    {
        var ticket = await StartHandler(Tasks(Ticket).Object, Interactive("ext-admin", RoleService.RoleNames.Admin))
            .Handle(new StartAiActorThinkCommand { ActorId = ActorId }, CancellationToken.None);

        ticket.Should().Be(Ticket);
    }

    [Test]
    public async Task Start_BySomeoneElse_IsRefused_WithoutStartingATask()
    {
        var tasks = Tasks(Ticket);

        var start = () => StartHandler(tasks.Object, Interactive("ext-ben", RoleService.RoleNames.Editor))
            .Handle(new StartAiActorThinkCommand { ActorId = ActorId }, CancellationToken.None);

        await start.Should().ThrowAsync<BeaconForbiddenException>();
        tasks.Verify(x => x.StartAsync(It.IsAny<BackgroundTaskKind>(), It.IsAny<int>(), It.IsAny<Func<int, SecurityAuditEntry>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Start_WhileACycleOfTheActorIsQueuedOrRunning_AnswersRateLimited_AndIsNotOnRecord()
    {
        var audit = new RecordingSecurityAudit();

        var start = () => StartHandler(Tasks(null, audit).Object, Interactive("ext-ana"))
            .Handle(new StartAiActorThinkCommand { ActorId = ActorId }, CancellationToken.None);

        await start.Should().ThrowAsync<BeaconRateLimitedException>().WithMessage("*already queued or running*");
        audit.Ways.Should().BeEmpty();
    }

    [Test]
    public async Task TheTask_RunsTheActorsThinkCycle_AsTheRequester()
    {
        var outcome = await Executor(Interactive("ext-ana")).ExecuteAsync(new BackgroundTaskContext(1, ActorId, 1), CancellationToken.None);

        outcome.SkippedCode.Should().BeNull();
        _service.Verify(x => x.ExecuteThinkCycleAsync(ActorId, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task TheTask_WhenTheRequesterIsNoLongerTheCreatorOrAnAdmin_IsSkippedAsForbidden_WithoutThinking()
    {
        var outcome = await Executor(Interactive("ext-ben", RoleService.RoleNames.Editor)).ExecuteAsync(new BackgroundTaskContext(1, ActorId, 2), CancellationToken.None);

        outcome.SkippedCode.Should().Be(BackgroundTaskFailureCodes.Forbidden);
        _service.Verify(x => x.ExecuteThinkCycleAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task TheTask_RunAsAnAdminWhoIsNotTheCreator_RunsTheCycle()
    {
        var runAsAdmin = new BeaconActor("ext-admin", false)
        {
            Kind = BeaconPrincipalKind.Owner,
            BeaconUserId = 5,
            IsActiveUser = true,
            IsLiveAdmin = true,
            RoleLevel = 3
        };

        var outcome = await ExecutorFor(runAsAdmin).ExecuteAsync(new BackgroundTaskContext(1, ActorId, 5), CancellationToken.None);

        outcome.SkippedCode.Should().BeNull();
        _service.Verify(x => x.ExecuteThinkCycleAsync(ActorId, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task TheTask_RunAsAUserWhoIsNoLongerAnAdmin_IsSkippedAsForbidden()
    {
        var runAsFormerAdmin = new BeaconActor("ext-admin", false)
        {
            Kind = BeaconPrincipalKind.Owner,
            BeaconUserId = 5,
            IsActiveUser = true,
            IsLiveAdmin = false,
            RoleLevel = 1
        };

        var outcome = await ExecutorFor(runAsFormerAdmin).ExecuteAsync(new BackgroundTaskContext(1, ActorId, 5), CancellationToken.None);

        outcome.SkippedCode.Should().Be(BackgroundTaskFailureCodes.Forbidden);
        _service.Verify(x => x.ExecuteThinkCycleAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase(AiActorStatus.Paused)]
    [TestCase(AiActorStatus.Archived)]
    public async Task TheTask_ForAnActorPausedOrArchivedMeanwhile_IsSkippedAsInactive_WithoutThinking(AiActorStatus status)
    {
        _actors[0].Status = status;

        var outcome = await Executor(Interactive("ext-ana")).ExecuteAsync(new BackgroundTaskContext(1, ActorId, 1), CancellationToken.None);

        outcome.SkippedCode.Should().Be(BackgroundTaskFailureCodes.Inactive);
        _service.Verify(x => x.ExecuteThinkCycleAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task TheTask_ForAnActorThatIsGone_IsSkippedAsNotFound()
    {
        var outcome = await Executor(Interactive("ext-admin", RoleService.RoleNames.Admin)).ExecuteAsync(new BackgroundTaskContext(1, 404, 5), CancellationToken.None);

        outcome.SkippedCode.Should().Be(BackgroundTaskFailureCodes.NotFound);
        _service.Verify(x => x.ExecuteThinkCycleAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase(AiActorService.ThinkCooldownMessage + " Try again in 3 minute(s).", AiActorExecutionPhase.Failed, BackgroundTaskFailureCodes.Cooldown)]
    [TestCase(AiActorService.ActorUnavailableMessage, AiActorExecutionPhase.Failed, BackgroundTaskFailureCodes.NotFound)]
    [TestCase(AiActorService.OwnerNotAuthorizedMessage, AiActorExecutionPhase.Skipped, BackgroundTaskFailureCodes.Forbidden)]
    [TestCase("Skipped: a plan is waiting for a decision.", AiActorExecutionPhase.Skipped, BackgroundTaskFailureCodes.PlanPending)]
    public async Task TheTask_WhenTheCycleDoesNotRun_IsSkippedWithTheReasonsCode(string message, AiActorExecutionPhase phase, string code)
    {
        _service
            .Setup(x => x.ExecuteThinkCycleAsync(ActorId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiActorThinkResult { Success = false, ErrorMessage = message, Phase = phase });

        var outcome = await Executor(Interactive("ext-ana")).ExecuteAsync(new BackgroundTaskContext(1, ActorId, 1), CancellationToken.None);

        outcome.SkippedCode.Should().Be(code);
    }

    [Test]
    public async Task TheTask_WhenTheCycleFails_Throws_SoTheTaskIsRecordedFailed_WithoutTheMessage()
    {
        _service
            .Setup(x => x.ExecuteThinkCycleAsync(ActorId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiActorThinkResult { Success = false, ErrorMessage = "provider said: secret detail", Phase = AiActorExecutionPhase.Failed });

        var run = () => Executor(Interactive("ext-ana")).ExecuteAsync(new BackgroundTaskContext(1, ActorId, 1), CancellationToken.None);

        var thrown = await run.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().NotContain("secret detail");
        BackgroundTaskFailureCodes.Of(thrown.Which).Should().Be(BackgroundTaskFailureCodes.Failed);
    }

    [Test]
    public void TheTask_IsTheThinkKind_GivenUpAfterHalfAnHour()
    {
        var executor = Executor(Interactive("ext-ana"));

        executor.Kind.Should().Be(BackgroundTaskKind.AiActorThink);
        executor.AbandonAfter.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Test]
    public async Task Status_IsReadForTheActorsThinkKind()
    {
        var status = new BackgroundTaskStatus(31, "job-1", BackgroundTaskState.Running, DateTime.UtcNow, DateTime.UtcNow, null, null, null);
        var tasks = new Mock<IBackgroundTaskService>();
        tasks
            .Setup(x => x.GetStatusAsync(BackgroundTaskKind.AiActorThink, ActorId, 31, It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var read = await new GetAiActorThinkTaskHandler(tasks.Object)
            .Handle(new GetAiActorThinkTaskQuery(ActorId, 31), CancellationToken.None);

        read.Should().BeSameAs(status);
    }

    private static Mock<IBackgroundTaskService> Tasks(BackgroundTaskTicket? ticket, ISecurityAuditRecorder? audit = null)
    {
        var tasks = new Mock<IBackgroundTaskService>();
        tasks
            .Setup(x => x.StartAsync(BackgroundTaskKind.AiActorThink, ActorId, It.IsAny<Func<int, SecurityAuditEntry>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (BackgroundTaskKind _, int _, Func<int, SecurityAuditEntry>? auditEvent, CancellationToken cancellationToken) =>
            {
                if (ticket != null && audit != null && auditEvent != null)
                {
                    await audit.RecordAsync(auditEvent(ticket.TaskId), cancellationToken);
                }

                return ticket;
            });

        return tasks;
    }

    private StartAiActorThinkHandler StartHandler(IBackgroundTaskService tasks, ClaimsPrincipal caller)
        => new(tasks, Actor(caller), Factory(), NullLogger<StartAiActorThinkHandler>.Instance);

    private AiActorThinkTaskExecutor Executor(ClaimsPrincipal caller)
        => new(_service.Object, Actor(caller), Factory(), NullLogger<AiActorThinkTaskExecutor>.Instance);

    private AiActorThinkTaskExecutor ExecutorFor(BeaconActor caller)
    {
        var accessor = new Mock<IBeaconActorAccessor>();
        accessor
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(caller);

        return new(_service.Object, accessor.Object, Factory(), NullLogger<AiActorThinkTaskExecutor>.Instance);
    }

    private BeaconActorAccessor Actor(ClaimsPrincipal caller)
        => TestProjectAccess.ActorAccessor(TestRequestPrincipal.For(caller), Factory());

    private IDbContextFactory<BeaconContext> Factory()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(AiActor)] = RecordingBeaconContext.MemorySet(_actors, []),
                    [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(_users, [])
                },
                []));

        return factory.Object;
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
