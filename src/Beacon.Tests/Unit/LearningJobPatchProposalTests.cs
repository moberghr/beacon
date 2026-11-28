using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Services;
using Beacon.Core.Worker.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The learning aggregation job proposes documentation patches once the aggregation is done (SC5). Proposing them is a
/// step after the aggregation, not part of it: a failure to propose is logged by its failure code only and leaves the job
/// green, while the caller's cancellation still ends it. The job reads no database itself (the strict factory throws on
/// any use).
/// </summary>
[TestFixture]
public class LearningJobPatchProposalTests
{
    [Test]
    public async Task AggregateLearnedPatterns_ProposesPatches_AfterTheAggregation_WithTheJobsToken()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        var learning = new Mock<IMcpLearningAggregationService>(MockBehavior.Strict);
        learning
            .Setup(x => x.AggregateLearnedPatternsAsync(cancellation.Token))
            .Callback(() => calls.Add("aggregate"))
            .Returns(Task.CompletedTask);
        learning
            .Setup(x => x.GenerateDocumentationPatchesAsync(cancellation.Token))
            .Callback(() => calls.Add("patches"))
            .Returns(Task.CompletedTask);
        using var log = new LogRecorder();

        await CreateJobService(learning.Object, log).AggregateLearnedPatterns(cancellation.Token);

        calls.Should().Equal("aggregate", "patches");
        learning.VerifyAll();
        learning.VerifyNoOtherCalls();
    }

    [Test]
    public async Task AggregateLearnedPatterns_APatchFailure_IsLoggedByItsCodeOnly_AndTheJobSucceeds()
    {
        var learning = new Mock<IMcpLearningAggregationService>(MockBehavior.Strict);
        learning
            .Setup(x => x.AggregateLearnedPatternsAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        learning
            .Setup(x => x.GenerateDocumentationPatchesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("patch failed for public.orders with SELECT secret"));
        using var log = new LogRecorder();

        var act = () => CreateJobService(learning.Object, log).AggregateLearnedPatterns(CancellationToken.None);

        await act.Should().NotThrowAsync();
        var entry = log.Entries.Should().ContainSingle(x => x.Level == LogLevel.Error).Subject;
        entry.Message.Should().Contain("Documentation patches were not proposed").And.Contain("InvalidOperationException (error code");
        entry.FullText.Should().NotContain("secret").And.NotContain("public.orders");
        entry.Exception.Should().BeNull();
    }

    [Test]
    public async Task AggregateLearnedPatterns_CancelledWhileProposing_EndsTheJob()
    {
        using var cancellation = new CancellationTokenSource();
        var learning = new Mock<IMcpLearningAggregationService>(MockBehavior.Strict);
        learning
            .Setup(x => x.AggregateLearnedPatternsAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        learning
            .Setup(x => x.GenerateDocumentationPatchesAsync(It.IsAny<CancellationToken>()))
            .Callback(cancellation.Cancel)
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        using var log = new LogRecorder();

        var act = () => CreateJobService(learning.Object, log).AggregateLearnedPatterns(cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        log.Entries.Should().NotContain(x => x.Level == LogLevel.Error);
    }

    [Test]
    public async Task AggregateLearnedPatterns_ACancellationThatIsNotTheJobs_IsLoggedLikeAnyFailure()
    {
        // A timeout inside the patch step (not the job's own token) is a failure of that step, not the end of the job.
        var learning = new Mock<IMcpLearningAggregationService>(MockBehavior.Strict);
        learning
            .Setup(x => x.AggregateLearnedPatternsAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        learning
            .Setup(x => x.GenerateDocumentationPatchesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException());
        using var log = new LogRecorder();

        var act = () => CreateJobService(learning.Object, log).AggregateLearnedPatterns(CancellationToken.None);

        await act.Should().NotThrowAsync();
        log.Entries.Should().ContainSingle(x => x.Level == LogLevel.Error)
            .Which.Message.Should().Contain("TaskCanceledException");
    }

    [Test]
    public async Task AggregateLearnedPatterns_AnAggregationFailure_FailsTheJob_AndProposesNothing()
    {
        var learning = new Mock<IMcpLearningAggregationService>(MockBehavior.Strict);
        learning
            .Setup(x => x.AggregateLearnedPatternsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("aggregation failed"));
        using var log = new LogRecorder();

        var act = () => CreateJobService(learning.Object, log).AggregateLearnedPatterns(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("aggregation failed");
        learning.Verify(x => x.GenerateDocumentationPatchesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static JobService CreateJobService(IMcpLearningAggregationService learning, LogRecorder log)
    {
        return new JobService(
            new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict).Object,
            Mock.Of<IQueryService>(),
            Mock.Of<INotificationService>(),
            Mock.Of<ITaskService>(),
            Mock.Of<IAnomalyDetectionService>(),
            Mock.Of<IDataQualityEvaluationService>(),
            log.For<JobService>(),
            new RunAsScope(TestRequestPrincipal.OffRequest()),
            TestChangeApprovals.Deliveries(),
            mcpLearningService: learning,
            requestPrincipal: TestRequestPrincipal.OffRequest());
    }
}
