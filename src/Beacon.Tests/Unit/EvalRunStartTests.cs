using FluentAssertions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Handlers.McpEval;
using Beacon.Core.Services;
using Beacon.Core.Worker;

namespace Beacon.Tests.Unit;

/// <summary>An eval run covers one project's golden cases: a request without a project is refused (400) before anything starts.</summary>
[TestFixture]
public class EvalRunStartTests
{
    [Test]
    public async Task RunEval_WithoutAProject_IsRefused_AndNothingStartsOrIsQueued()
    {
        var evalService = new Mock<IMcpEvalService>(MockBehavior.Strict);
        var scheduler = new Mock<IBeaconScheduler>(MockBehavior.Strict);
        var handler = new RunEvalHandler(evalService.Object, scheduler.Object);

        var act = () => handler.Handle(new RunEvalCommand(null, 3), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(RunEvalHandler.ProjectRequiredMessage);
        evalService.VerifyNoOtherCalls();
        scheduler.VerifyNoOtherCalls();
    }

    [Test]
    public async Task RunEval_ForAProject_StartsAndQueuesTheRun()
    {
        var evalService = new Mock<IMcpEvalService>();
        evalService
            .Setup(x => x.StartRunAsync(5, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(12);
        var scheduler = new Mock<IBeaconScheduler>();
        var handler = new RunEvalHandler(evalService.Object, scheduler.Object);

        var result = await handler.Handle(new RunEvalCommand(5, 3), CancellationToken.None);

        result.RunId.Should().Be(12);
        scheduler.Verify(x => x.EnqueueMcpEval(12), Times.Once);
    }
}
