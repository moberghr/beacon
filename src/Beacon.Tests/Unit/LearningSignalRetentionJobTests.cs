using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Services;
using Beacon.Core.Worker.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The recurring retention job asks for every project's own window (<c>retentionDays: 0</c>) and runs whatever the learning
/// switch says: it reads no settings and no database itself (the strict factory throws on any use).
/// </summary>
[TestFixture]
public class LearningSignalRetentionJobTests
{
    [Test]
    public async Task CleanupOldSignals_AsksForEachProjectsOwnWindow_WithoutALearningGate()
    {
        using var cancellation = new CancellationTokenSource();
        var learning = new Mock<IMcpLearningAggregationService>(MockBehavior.Strict);
        learning
            .Setup(x => x.CleanupOldSignalsAsync(0, cancellation.Token))
            .Returns(Task.CompletedTask);

        await CreateJobService(learning.Object).CleanupOldSignals(cancellation.Token);

        learning.Verify(x => x.CleanupOldSignalsAsync(0, cancellation.Token), Times.Once);
        learning.VerifyNoOtherCalls();
    }

    [Test]
    public async Task CleanupOldSignals_AFailedCleanup_FailsTheJob()
    {
        var learning = new Mock<IMcpLearningAggregationService>();
        learning
            .Setup(x => x.CleanupOldSignalsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cleanup failed"));

        var act = () => CreateJobService(learning.Object).CleanupOldSignals(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("cleanup failed");
    }

    private static JobService CreateJobService(IMcpLearningAggregationService learning)
    {
        return new JobService(
            new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict).Object,
            Mock.Of<IQueryService>(),
            Mock.Of<INotificationService>(),
            Mock.Of<ITaskService>(),
            Mock.Of<IAnomalyDetectionService>(),
            Mock.Of<IDataQualityEvaluationService>(),
            NullLogger<JobService>.Instance,
            new RunAsScope(new HttpContextAccessor()),
            TestChangeApprovals.Deliveries(),
            mcpLearningService: learning,
            httpContextAccessor: new HttpContextAccessor());
    }
}
