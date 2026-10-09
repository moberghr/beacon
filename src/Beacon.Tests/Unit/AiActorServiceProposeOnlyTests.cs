using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class AiActorServiceProposeOnlyTests
{
    private Mock<IDbContextFactory<BeaconContext>> _contextFactory = null!;
    private Mock<IQueryService> _queryService = null!;
    private Mock<ISubscriptionService> _subscriptionService = null!;
    private AiActorService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _contextFactory = new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict);
        _queryService = new Mock<IQueryService>(MockBehavior.Strict);
        _subscriptionService = new Mock<ISubscriptionService>(MockBehavior.Strict);

        _service = new AiActorService(
            _contextFactory.Object,
            Mock.Of<ILlmProvider>(MockBehavior.Strict),
            Mock.Of<IDatabaseMetadataService>(MockBehavior.Strict),
            _queryService.Object,
            _subscriptionService.Object,
            Mock.Of<IBeaconScheduler>(MockBehavior.Strict),
            Mock.Of<ILogger<AiActorService>>());
    }

    [TestCase("CREATE_QUERY", AiActorActionType.CreateQuery)]
    [TestCase("CREATE_SUBSCRIPTION", AiActorActionType.CreateSubscription)]
    [TestCase("REFINE_QUERY", AiActorActionType.RefineQuery)]
    [TestCase("ARCHIVE_QUERY", AiActorActionType.ArchiveQuery)]
    [TestCase("ARCHIVE_SUBSCRIPTION", AiActorActionType.ArchiveSubscription)]
    public async Task ExecuteOrProposeAsync_RequiresApproval_ProposesAndTouchesNothing(string type, AiActorActionType expected)
    {
        var actor = new Beacon.Core.Data.Entities.AiActor { Id = 5, RequiresApproval = true };
        var plan = new AiActorActionPlan
        {
            ActionType = type,
            Reasoning = "r"
        };
        plan.Parameters["queryId"] = 1;
        plan.Parameters["subscriptionId"] = 2;
        plan.Parameters["name"] = "n";
        plan.Parameters["sql"] = "SELECT 1";

        var action = await _service.ExecuteOrProposeAsync(actor, plan, new Dictionary<string, int>(), CancellationToken.None);

        action.Proposed.Should().BeTrue();
        action.Success.Should().BeFalse();
        action.ActionType.Should().Be(expected);
        action.Reasoning.Should().Be("r");
        _contextFactory.VerifyNoOtherCalls();
        _queryService.VerifyNoOtherCalls();
        _subscriptionService.VerifyNoOtherCalls();
    }

    // "actionType": null / "parameters": null in the LLM's JSON used to throw out of the propose branch and fail the
    // whole cycle; it is one failed action, like any other malformed action.
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task ExecuteOrProposeAsync_RequiresApproval_NullActionTypeOrParameters_ReturnsFailedAction(bool nullType, bool nullParameters)
    {
        var plan = new AiActorActionPlan
        {
            ActionType = nullType ? null! : "ARCHIVE_QUERY",
            Reasoning = "r",
            Parameters = nullParameters ? null! : new Dictionary<string, object?> { ["queryId"] = 1 }
        };

        var action = await _service.ExecuteOrProposeAsync(ApprovalActor(), plan, new Dictionary<string, int>(), CancellationToken.None);

        action.Proposed.Should().BeFalse();
        action.Success.Should().BeFalse();
        action.ErrorMessage.Should().Be("Unknown action type");
        AssertNothingTouched();
    }

    [Test]
    public async Task ExecuteOrProposeAsync_RequiresApproval_NullPlan_ReturnsFailedActionInsteadOfThrowing()
    {
        // "actions": [null] deserializes to a null element.
        var action = await _service.ExecuteOrProposeAsync(ApprovalActor(), null!, new Dictionary<string, int>(), CancellationToken.None);

        action.Proposed.Should().BeFalse();
        action.Success.Should().BeFalse();
        action.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        AssertNothingTouched();
    }

    [Test]
    public async Task ExecuteOrProposeAsync_Executing_CancelledMidAction_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        _contextFactory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<BeaconContext>(ct);
            });

        var act = () => _service.ExecuteOrProposeAsync(ExecutingActor(), ArchivePlan(), new Dictionary<string, int>(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _queryService.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ExecuteOrProposeAsync_Executing_CancellationNotRequestedByCaller_IsAFailedAction()
    {
        // e.g. an HTTP timeout inside the action: not the caller's cancellation, so it stays a per-action failure.
        _contextFactory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.FromException<BeaconContext>(new OperationCanceledException("inner timeout")));

        var action = await _service.ExecuteOrProposeAsync(ExecutingActor(), ArchivePlan(), new Dictionary<string, int>(), CancellationToken.None);

        action.Success.Should().BeFalse();
        action.Proposed.Should().BeFalse();
        action.ErrorMessage.Should().Be("inner timeout");
        _queryService.VerifyNoOtherCalls();
    }

    private void AssertNothingTouched()
    {
        _contextFactory.VerifyNoOtherCalls();
        _queryService.VerifyNoOtherCalls();
        _subscriptionService.VerifyNoOtherCalls();
    }

    private static Beacon.Core.Data.Entities.AiActor ApprovalActor() =>
        new()
        {
            Id = 5,
            RequiresApproval = true
        };

    private static Beacon.Core.Data.Entities.AiActor ExecutingActor() =>
        new()
        {
            Id = 5,
            RequiresApproval = false
        };

    private static AiActorActionPlan ArchivePlan() =>
        new()
        {
            ActionType = "ARCHIVE_QUERY",
            Reasoning = "r",
            Parameters = new Dictionary<string, object?> { ["queryId"] = 1 }
        };
}
