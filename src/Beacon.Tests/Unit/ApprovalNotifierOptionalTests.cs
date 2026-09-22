using Beacon.Core.Handlers.Approvals;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The approval handlers take <c>IApprovalNotifier</c> optionally so a host that never registers
/// one (Beacon.Api supplies it via AddBeaconApiServices) can still boot. The decision itself must
/// be persisted either way — the push is a side-channel, not part of the transaction.
/// </summary>
[TestFixture]
public class ApprovalNotifierOptionalTests
{
    private static Mock<IQueryApprovalService> ServiceReturning(string? requestedBy)
    {
        var service = new Mock<IQueryApprovalService>();
        service
            .Setup(x => x.GetApprovalDetailAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApprovalRequestDetail { Id = 7, RequestedByUserId = requestedBy });
        return service;
    }

    [Test]
    public async Task Approve_PushesApprovedToReviewerAndRequester()
    {
        var service = ServiceReturning("requester-1");
        var notifier = new Mock<IApprovalNotifier>();
        var handler = new ApproveQueryChangeHandler(service.Object, NullLogger<ApproveQueryChangeHandler>.Instance, notifier.Object);

        await handler.Handle(
            new ApproveQueryChangeCommand { RequestId = 7, ReviewerUserId = "reviewer-9" },
            CancellationToken.None);

        service.Verify(x => x.ApproveAsync(7, "reviewer-9", null, null, It.IsAny<CancellationToken>()), Times.Once);
        notifier.Verify(
            x => x.ApprovalUpdatedAsync(7, "approved", "reviewer-9", "requester-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Reject_PushesRejected()
    {
        var service = ServiceReturning("requester-1");
        var notifier = new Mock<IApprovalNotifier>();
        var handler = new RejectQueryChangeHandler(service.Object, NullLogger<RejectQueryChangeHandler>.Instance, notifier.Object);

        await handler.Handle(
            new RejectQueryChangeCommand { RequestId = 7, ReviewerUserId = "reviewer-9" },
            CancellationToken.None);

        service.Verify(x => x.RejectAsync(7, "reviewer-9", null, null, It.IsAny<CancellationToken>()), Times.Once);
        notifier.Verify(
            x => x.ApprovalUpdatedAsync(7, "rejected", "reviewer-9", "requester-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Approve_WithoutNotifier_StillApproves()
    {
        var service = ServiceReturning("requester-1");
        var logger = new RecordingLogger<ApproveQueryChangeHandler>();
        var handler = new ApproveQueryChangeHandler(service.Object, logger);

        var act = async () => await handler.Handle(
            new ApproveQueryChangeCommand { RequestId = 7, ReviewerUserId = "reviewer-9" },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        service.Verify(x => x.ApproveAsync(7, "reviewer-9", null, null, It.IsAny<CancellationToken>()), Times.Once);
        logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain("no IApprovalNotifier is registered");
    }

    [Test]
    public async Task Reject_WithoutNotifier_StillRejects()
    {
        var service = ServiceReturning("requester-1");
        var logger = new RecordingLogger<RejectQueryChangeHandler>();
        var handler = new RejectQueryChangeHandler(service.Object, logger);

        var act = async () => await handler.Handle(
            new RejectQueryChangeCommand { RequestId = 7, ReviewerUserId = "reviewer-9" },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        service.Verify(x => x.RejectAsync(7, "reviewer-9", null, null, It.IsAny<CancellationToken>()), Times.Once);
        logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain("no IApprovalNotifier is registered");
    }

    /// <summary>
    /// Moq cannot proxy ILogger&lt;T&gt; when T is internal (Castle needs InternalsVisibleTo for
    /// DynamicProxyGenAssembly2 on Beacon.Core), so record real calls instead of mocking.
    /// Skipping the push is acceptable; skipping it *silently* is not — an operator whose UI never
    /// live-updates needs a line in the log saying why.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
