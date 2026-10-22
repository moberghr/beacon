using System.Threading.Channels;
using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Exceptions;
using Beacon.Core.Models.Ai;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// The queue takes a slot for each attempt and gives it back before waiting to retry, bounds how long and how many
/// requests wait for a slot, keeps background requests to half the slots, and retries only transient failures.
/// </summary>
[TestFixture]
[CancelAfter(10_000)]
public class LlmRequestQueueSlotTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    [Test]
    public async Task ARequestBackingOff_HoldsNoSlot_SoAnotherRequestRuns()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, maxConcurrent: 1, jitter: () => 0.999);
        var backingOff = new Mock<ILlmProvider>();
        backingOff
            .SetupSequence(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmTransientException("overloaded"))
            .ReturnsAsync(new LlmResponse { Content = "first" });
        var other = Answering("second");

        var first = queue.EnqueueRequestAsync(backingOff.Object, new LlmRequest());
        var second = await queue.EnqueueRequestAsync(other.Object, new LlmRequest()).WaitAsync(Guard);

        second.Content.Should().Be("second");
        first.IsCompleted.Should().BeFalse("the first request is still waiting to retry");
        time.Advance(TimeSpan.FromSeconds(1));
        (await first.WaitAsync(Guard)).Content.Should().Be("first");
        backingOff.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task ARequestThatWaitsLongerThanTheWaitLimit_FailsAsBusy_WithoutReachingTheProvider()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, maxConcurrent: 1, limits: new ProviderLimits { InteractiveQueueWaitSeconds = 60 });
        var stalled = Stalling();
        var waiting = Answering("late");

        _ = queue.EnqueueRequestAsync(stalled.Provider.Object, new LlmRequest());
        var second = queue.EnqueueRequestAsync(waiting.Object, new LlmRequest());
        time.Advance(TimeSpan.FromSeconds(59));
        second.IsCompleted.Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(1));

        var thrown = await second.Invoking(x => x.WaitAsync(Guard)).Should().ThrowExactlyAsync<LlmQueueSaturatedException>();
        thrown.Which.Message.Should().Be("The LLM is busy. Try again later.");
        waiting.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ABackgroundRequest_WaitsForItsOwnLimit()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, maxConcurrent: 2, limits: new ProviderLimits { InteractiveQueueWaitSeconds = 60, BackgroundQueueWaitSeconds = 300 });
        _ = queue.EnqueueRequestAsync(Stalling().Provider.Object, Background());

        var second = queue.EnqueueRequestAsync(Answering("late").Object, Background());
        time.Advance(TimeSpan.FromSeconds(299));
        second.IsCompleted.Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(1));

        await second.Invoking(x => x.WaitAsync(Guard)).Should().ThrowExactlyAsync<LlmQueueSaturatedException>();
    }

    [Test]
    public async Task ARequestPastTheWaiterLimit_FailsAsBusyAtOnce()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, maxConcurrent: 1, limits: new ProviderLimits { MaxQueuedRequests = 1 });
        var third = Answering("never");

        _ = queue.EnqueueRequestAsync(Stalling().Provider.Object, new LlmRequest());
        var waiting = queue.EnqueueRequestAsync(Answering("waits").Object, new LlmRequest());
        var rejected = queue.EnqueueRequestAsync(third.Object, new LlmRequest());

        rejected.IsFaulted.Should().BeTrue("a request past the waiter limit does not wait");
        await rejected.Invoking(x => x).Should().ThrowExactlyAsync<LlmQueueSaturatedException>();
        waiting.IsCompleted.Should().BeFalse();
        third.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task BackgroundRequests_HoldAtMostHalfTheSlots_AndInteractiveRequestsStillRun()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, maxConcurrent: 4);
        var background = Answering("background");
        _ = queue.EnqueueRequestAsync(Stalling().Provider.Object, Background());
        _ = queue.EnqueueRequestAsync(Stalling().Provider.Object, Background());

        var thirdBackground = queue.EnqueueRequestAsync(background.Object, Background());
        var interactive = Stalling();
        var otherInteractive = Stalling();
        _ = queue.EnqueueRequestAsync(interactive.Provider.Object, new LlmRequest());
        _ = queue.EnqueueRequestAsync(otherInteractive.Provider.Object, new LlmRequest());

        await interactive.Started.Task.WaitAsync(Guard);
        await otherInteractive.Started.Task.WaitAsync(Guard);
        thirdBackground.IsCompleted.Should().BeFalse();
        background.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task WithOneSlot_ABackgroundRequestStillRuns()
    {
        var queue = CreateQueue(new FakeTimeProvider(), maxConcurrent: 1);

        var response = await queue.EnqueueRequestAsync(Answering("ok").Object, Background()).WaitAsync(Guard);

        response.Content.Should().Be("ok");
    }

    [Test]
    public async Task AFinishedBackgroundRequest_GivesItsSlotBack()
    {
        var queue = CreateQueue(new FakeTimeProvider(), maxConcurrent: 2);

        for (var i = 0; i < 3; i++)
        {
            (await queue.EnqueueRequestAsync(Answering("ok").Object, Background()).WaitAsync(Guard)).Content.Should().Be("ok");
        }
    }

    [Test]
    public async Task ATransientFailure_IsTriedThreeTimes_ThenPropagates()
    {
        var queue = CreateQueue(new FakeTimeProvider(), maxConcurrent: 1, jitter: () => 0);
        var provider = new Mock<ILlmProvider>();
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmTransientException("overloaded"));

        var act = () => queue.EnqueueRequestAsync(provider.Object, new LlmRequest()).WaitAsync(Guard);

        await act.Should().ThrowExactlyAsync<LlmTransientException>();
        provider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Test]
    public async Task ARetryAfter_IsWaitedBeforeTheNextAttempt()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, maxConcurrent: 1, jitter: () => 0);
        var provider = new Mock<ILlmProvider>();
        provider
            .SetupSequence(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmTransientException("rate limited", TimeSpan.FromSeconds(10)))
            .ReturnsAsync(new LlmResponse { Content = "ok" });

        var call = queue.EnqueueRequestAsync(provider.Object, new LlmRequest());
        time.Advance(TimeSpan.FromSeconds(9));
        call.IsCompleted.Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(1));

        (await call.WaitAsync(Guard)).Content.Should().Be("ok");
    }

    [Test]
    public async Task ARetryAfterLongerThanTheCap_IsNotRetried()
    {
        var queue = CreateQueue(new FakeTimeProvider(), maxConcurrent: 1, jitter: () => 0);
        var provider = new Mock<ILlmProvider>();
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmTransientException("rate limited", TimeSpan.FromSeconds(61)));

        var act = () => queue.EnqueueRequestAsync(provider.Object, new LlmRequest()).WaitAsync(Guard);

        (await act.Should().ThrowExactlyAsync<LlmTransientException>()).Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(61));
        provider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase(0, 0.0, 0.0)]
    [TestCase(0, 0.5, 0.5)]
    [TestCase(1, 0.5, 1.0)]
    [TestCase(2, 0.999, 3.996)]
    [TestCase(5, 0.5, 15.0)]
    [TestCase(10, 0.5, 15.0)]
    [TestCase(40, 0.999, 29.97)]
    public void TheBackoff_IsFullJitterOverAnExponentialCappedAtThirtySeconds(int attempt, double sample, double expectedSeconds)
    {
        LlmRequestQueue.BackoffDelay(attempt, sample).TotalSeconds.Should().BeApproximately(expectedSeconds, 0.001);
    }

    [Test]
    public void TheBackoff_StaysWithinItsBounds()
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var ceiling = Math.Min(30, Math.Pow(2, attempt));

            LlmRequestQueue.BackoffDelay(attempt, 0).Should().Be(TimeSpan.Zero);
            LlmRequestQueue.BackoffDelay(attempt, 0.9999999).TotalSeconds.Should().BeLessThan(ceiling).And.BeGreaterThan(0);
        }
    }

    [Test]
    public async Task ACallerCancellation_IsNotRetried_AndPropagates()
    {
        var queue = CreateQueue(new FakeTimeProvider(), maxConcurrent: 1, jitter: () => 0);
        using var caller = new CancellationTokenSource();
        var provider = new Mock<ILlmProvider>();
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns((LlmRequest _, CancellationToken ct) =>
            {
                caller.Cancel();
                return Task.FromException<LlmResponse>(new OperationCanceledException(ct));
            });

        var act = () => queue.EnqueueRequestAsync(provider.Object, new LlmRequest(), caller.Token).WaitAsync(Guard);

        await act.Should().ThrowAsync<OperationCanceledException>();
        provider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AProviderTimeout_WhileTheCallerWaits_IsRetried()
    {
        var queue = CreateQueue(new FakeTimeProvider(), maxConcurrent: 1, jitter: () => 0);
        var provider = new Mock<ILlmProvider>();
        provider
            .SetupSequence(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()))
            .ReturnsAsync(new LlmResponse { Content = "ok" });

        var response = await queue.EnqueueRequestAsync(provider.Object, new LlmRequest()).WaitAsync(Guard);

        response.Content.Should().Be("ok");
        provider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task AFailureWhoseMessageMentionsATimeout_IsNotRetried_ByItsWording()
    {
        var queue = CreateQueue(new FakeTimeProvider(), maxConcurrent: 1, jitter: () => 0);
        var provider = new Mock<ILlmProvider>();
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiServiceException("upstream said 429 rate limit timeout 503"));

        var act = () => queue.EnqueueRequestAsync(provider.Object, new LlmRequest()).WaitAsync(Guard);

        await act.Should().ThrowExactlyAsync<AiServiceException>();
        provider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task TheSlot_IsReturnedAfterAFailure()
    {
        var queue = CreateQueue(new FakeTimeProvider(), maxConcurrent: 1);
        var failing = new Mock<ILlmProvider>();
        failing
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bad request shape"));

        await queue.Invoking(x => x.EnqueueRequestAsync(failing.Object, new LlmRequest())).Should().ThrowAsync<InvalidOperationException>();
        var response = await queue.EnqueueRequestAsync(Answering("ok").Object, new LlmRequest()).WaitAsync(Guard);

        response.Content.Should().Be("ok");
    }

    [Test]
    public async Task ABackgroundRequest_ThatTimesOutWaitingForAMainSlot_GivesItsBackgroundSlotBack()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, maxConcurrent: 2, limits: new ProviderLimits { BackgroundQueueWaitSeconds = 300 });
        var first = Held();
        var second = Held();
        _ = queue.EnqueueRequestAsync(first.Provider.Object, new LlmRequest());
        _ = queue.EnqueueRequestAsync(second.Provider.Object, new LlmRequest());

        // The one background slot is free, the main slots are not: the request takes the background slot and waits.
        var timedOut = queue.EnqueueRequestAsync(Answering("never").Object, Background());
        time.Advance(TimeSpan.FromSeconds(300));
        await timedOut.Invoking(x => x.WaitAsync(Guard)).Should().ThrowExactlyAsync<LlmQueueSaturatedException>();

        first.Finish.SetResult(new LlmResponse { Content = "done" });
        var next = await queue.EnqueueRequestAsync(Answering("next").Object, Background()).WaitAsync(Guard);

        next.Content.Should().Be("next", "the timed-out request gave the only background slot back");
    }

    [Test]
    public async Task ACallerCancellation_WhileQueued_IsACancellation_AndFreesTheWaiterAndBackgroundSlot()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, maxConcurrent: 2, limits: new ProviderLimits { MaxQueuedRequests = 2 });
        var first = Held();
        _ = queue.EnqueueRequestAsync(first.Provider.Object, new LlmRequest());
        _ = queue.EnqueueRequestAsync(Held().Provider.Object, new LlmRequest());
        using var caller = new CancellationTokenSource();
        var cancelled = Answering("never");

        var waiting = queue.EnqueueRequestAsync(cancelled.Object, Background(), caller.Token);
        await caller.CancelAsync();

        await waiting.Invoking(x => x.WaitAsync(Guard)).Should().ThrowAsync<OperationCanceledException>();
        cancelled.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // One background waiter and one background slot: both came back, so the next background request queues and runs.
        var next = queue.EnqueueRequestAsync(Answering("next").Object, Background());
        next.IsCompleted.Should().BeFalse("it waits for a main slot rather than being turned away");
        first.Finish.SetResult(new LlmResponse { Content = "done" });
        (await next.WaitAsync(Guard)).Content.Should().Be("next");
    }

    [Test]
    public async Task APreCancelledCall_IsACancellation_WithoutReachingTheProvider()
    {
        var queue = CreateQueue(new FakeTimeProvider(), maxConcurrent: 1);
        var provider = Answering("never");
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        var act = () => queue.EnqueueRequestAsync(provider.Object, new LlmRequest(), caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        provider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        (await queue.EnqueueRequestAsync(Answering("ok").Object, new LlmRequest()).WaitAsync(Guard)).Content.Should().Be("ok");
    }

    [Test]
    public async Task TheWaiterCount_IsRestoredAfterATimeoutAndARejection_AndAQueuedRequestRunsWhenASlotFrees()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, maxConcurrent: 1, limits: new ProviderLimits { MaxQueuedRequests = 1, InteractiveQueueWaitSeconds = 60 });
        var running = Held();
        _ = queue.EnqueueRequestAsync(running.Provider.Object, new LlmRequest());

        var timedOut = queue.EnqueueRequestAsync(Answering("never").Object, new LlmRequest());
        var rejected = queue.EnqueueRequestAsync(Answering("never").Object, new LlmRequest());
        await rejected.Invoking(x => x.WaitAsync(Guard)).Should().ThrowExactlyAsync<LlmQueueSaturatedException>();
        time.Advance(TimeSpan.FromSeconds(60));
        await timedOut.Invoking(x => x.WaitAsync(Guard)).Should().ThrowExactlyAsync<LlmQueueSaturatedException>();

        var queued = queue.EnqueueRequestAsync(Answering("queued").Object, new LlmRequest());
        queued.IsCompleted.Should().BeFalse("the only waiter place is free again");
        running.Finish.SetResult(new LlmResponse { Content = "done" });

        (await queued.WaitAsync(Guard)).Content.Should().Be("queued");
    }

    [Test]
    public async Task ACallerCancellation_DuringTheBackoff_EndsTheRequest_WithoutAnotherAttempt()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, maxConcurrent: 1, jitter: () => 0.999);
        var provider = new Mock<ILlmProvider>();
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmTransientException("overloaded"));
        using var caller = new CancellationTokenSource();

        var call = queue.EnqueueRequestAsync(provider.Object, new LlmRequest(), caller.Token);
        call.IsCompleted.Should().BeFalse("the request is waiting to retry");
        await caller.CancelAsync();

        await call.Invoking(x => x.WaitAsync(Guard)).Should().ThrowAsync<OperationCanceledException>();
        time.Advance(TimeSpan.FromSeconds(30));
        provider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        (await queue.EnqueueRequestAsync(Answering("ok").Object, new LlmRequest()).WaitAsync(Guard)).Content.Should().Be("ok");
    }

    [Test]
    public async Task ARetryThatFindsNoSlot_FailsAsBusy_WithTheProviderFailureAsItsCause()
    {
        var time = new TimerWatchingTimeProvider();
        var queue = CreateQueue(time, maxConcurrent: 1, limits: new ProviderLimits { InteractiveQueueWaitSeconds = 60 }, jitter: () => 0.999);
        var failure = new LlmTransientException("overloaded");
        var provider = new Mock<ILlmProvider>();
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var retrying = queue.EnqueueRequestAsync(provider.Object, new LlmRequest());
        var holder = Held();
        _ = queue.EnqueueRequestAsync(holder.Provider.Object, new LlmRequest());
        await holder.Started.Task.WaitAsync(Guard);
        time.Advance(TimeSpan.FromSeconds(1));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(60), Guard);
        retrying.IsCompleted.Should().BeFalse("the retry waits for the slot");
        time.Advance(TimeSpan.FromSeconds(60));

        var thrown = await retrying.Invoking(x => x.WaitAsync(Guard)).Should().ThrowExactlyAsync<LlmQueueSaturatedException>();
        thrown.Which.InnerException.Should().BeSameAs(failure);
        thrown.Which.Message.Should().Be("The LLM is busy. Try again later.");
        provider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task BackgroundWaiters_AreCappedAtHalfTheWaiterLimit_SoInteractiveCallsStillQueue()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, maxConcurrent: 1, limits: new ProviderLimits { MaxQueuedRequests = 4 });
        _ = queue.EnqueueRequestAsync(Held().Provider.Object, new LlmRequest());

        var backgroundWaiting = new[]
        {
            queue.EnqueueRequestAsync(Answering("b1").Object, Background()),
            queue.EnqueueRequestAsync(Answering("b2").Object, Background())
        };
        var backgroundRejected = queue.EnqueueRequestAsync(Answering("b3").Object, Background());
        var interactive = Enumerable.Range(0, 4)
            .Select(_ => queue.EnqueueRequestAsync(Answering("i").Object, new LlmRequest()))
            .ToList();
        var interactiveRejected = queue.EnqueueRequestAsync(Answering("i5").Object, new LlmRequest());

        backgroundWaiting.Should().OnlyContain(x => !x.IsCompleted);
        await backgroundRejected.Invoking(x => x.WaitAsync(Guard)).Should().ThrowExactlyAsync<LlmQueueSaturatedException>();
        interactive.Should().OnlyContain(x => !x.IsCompleted, "a background burst does not take the interactive waiter places");
        await interactiveRejected.Invoking(x => x.WaitAsync(Guard)).Should().ThrowExactlyAsync<LlmQueueSaturatedException>();
    }

    [TestCase(0)]
    [TestCase(-5)]
    public void NonPositiveLimits_TakeTheirDefaults(int value)
    {
        var queue = CreateQueue(
            new FakeTimeProvider(),
            maxConcurrent: value,
            limits: new ProviderLimits { InteractiveQueueWaitSeconds = value, BackgroundQueueWaitSeconds = value, MaxQueuedRequests = value });

        queue.InteractiveWait.Should().Be(TimeSpan.FromSeconds(60));
        queue.BackgroundWait.Should().Be(TimeSpan.FromSeconds(300));
    }

    [Test]
    public async Task ANonPositiveConcurrency_TakesTheDefault_SoRequestsRunSideBySide()
    {
        var queue = CreateQueue(new FakeTimeProvider(), maxConcurrent: 0);
        var calls = Enumerable.Range(0, 3).Select(_ => Held()).ToList();

        foreach (var call in calls)
        {
            _ = queue.EnqueueRequestAsync(call.Provider.Object, new LlmRequest());
        }

        foreach (var call in calls)
        {
            await call.Started.Task.WaitAsync(Guard);
        }
    }

    [Test]
    public async Task HugeWaitLimits_AreCappedAtAnHour()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(
            time,
            maxConcurrent: 1,
            limits: new ProviderLimits { InteractiveQueueWaitSeconds = int.MaxValue, BackgroundQueueWaitSeconds = int.MaxValue });
        _ = queue.EnqueueRequestAsync(Held().Provider.Object, new LlmRequest());

        queue.InteractiveWait.Should().Be(TimeSpan.FromHours(1));
        queue.BackgroundWait.Should().Be(TimeSpan.FromHours(1));
        var waiting = queue.EnqueueRequestAsync(Answering("late").Object, new LlmRequest());
        time.Advance(TimeSpan.FromSeconds(3599));
        waiting.IsCompleted.Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(1));

        await waiting.Invoking(x => x.WaitAsync(Guard)).Should().ThrowExactlyAsync<LlmQueueSaturatedException>();
    }

    [Test]
    public async Task TheOriginalConstructor_TakesTheConcurrency_AndTheTotalNumberOfAttempts()
    {
        var queue = new LlmRequestQueue(2, 1);
        var first = Held();
        var second = Held();
        _ = queue.EnqueueRequestAsync(first.Provider.Object, new LlmRequest());
        _ = queue.EnqueueRequestAsync(second.Provider.Object, new LlmRequest());
        await first.Started.Task.WaitAsync(Guard);
        await second.Started.Task.WaitAsync(Guard);
        first.Finish.SetResult(new LlmResponse { Content = "done" });
        var provider = new Mock<ILlmProvider>();
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmTransientException("overloaded"));

        await queue.Invoking(x => x.EnqueueRequestAsync(provider.Object, new LlmRequest()).WaitAsync(Guard)).Should().ThrowExactlyAsync<LlmTransientException>();

        provider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once, "one attempt in all, no retry");
    }

    [Test]
    public async Task ARetryAfter_IsTheLongerOfTheAskedWaitAndTheBackoff_WithUpToTenPercentOnTop()
    {
        LlmRequestQueue.RetryAfterDelay(TimeSpan.Zero, TimeSpan.FromSeconds(3), 0.5).Should().Be(TimeSpan.FromSeconds(3));
        LlmRequestQueue.RetryAfterDelay(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), 0).Should().Be(TimeSpan.FromSeconds(10));
        LlmRequestQueue.RetryAfterDelay(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), 0.5).Should().Be(TimeSpan.FromSeconds(10.5));
        LlmRequestQueue.RetryAfterDelay(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), 0.9999)!.Value.TotalSeconds.Should().BeLessThan(11);
        LlmRequestQueue.RetryAfterDelay(TimeSpan.FromSeconds(60), TimeSpan.Zero, 0.5).Should().Be(TimeSpan.FromSeconds(63));
        LlmRequestQueue.RetryAfterDelay(TimeSpan.FromSeconds(61), TimeSpan.Zero, 0).Should().BeNull();

        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, maxConcurrent: 1, jitter: () => 0.5);
        var provider = new Mock<ILlmProvider>();
        provider
            .SetupSequence(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmTransientException("rate limited", TimeSpan.Zero))
            .ReturnsAsync(new LlmResponse { Content = "ok" });

        var call = queue.EnqueueRequestAsync(provider.Object, new LlmRequest());
        time.Advance(TimeSpan.FromMilliseconds(499));
        call.IsCompleted.Should().BeFalse("a Retry-After of zero still waits the backoff");
        time.Advance(TimeSpan.FromMilliseconds(1));

        (await call.WaitAsync(Guard)).Content.Should().Be("ok");
    }

    [Test]
    public async Task Retries_AreLoggedWithTheFailureTypeOnly_AndExhaustionAsAWarning()
    {
        var logger = new Mock<ILogger<LlmRequestQueue>>();
        var queue = new LlmRequestQueue(new ProviderLimits { MaxConcurrentRequests = 1 }, new FakeTimeProvider(), () => 0, logger.Object);
        var provider = new Mock<ILlmProvider>();
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmTransientException("SECRET-PROVIDER-TEXT"));

        await queue.Invoking(x => x.EnqueueRequestAsync(provider.Object, new LlmRequest()).WaitAsync(Guard)).Should().ThrowExactlyAsync<LlmTransientException>();

        var logged = logger.Invocations
            .Where(x => x.Method.Name == nameof(ILogger.Log))
            .Select(x => (Level: (LogLevel)x.Arguments[0], Text: x.Arguments[2].ToString()!, Exception: x.Arguments[3]))
            .ToList();
        logged.Where(x => x.Level == LogLevel.Information).Should().HaveCount(2);
        logged.Where(x => x.Level == LogLevel.Warning).Should().ContainSingle().Which.Text.Should().Contain("3 attempts");
        logged.Should().OnlyContain(x => x.Text.Contains(nameof(LlmTransientException)) && !x.Text.Contains("SECRET-PROVIDER-TEXT") && x.Exception == null);
    }

    private static LlmRequestQueue CreateQueue(TimeProvider time, int maxConcurrent, ProviderLimits? limits = null, Func<double>? jitter = null)
    {
        return new LlmRequestQueue(
            (limits ?? new ProviderLimits()) with { MaxConcurrentRequests = maxConcurrent },
            time,
            jitter ?? (() => 0));
    }

    private static LlmRequest Background()
    {
        return new LlmRequest { Priority = LlmRequestPriority.Background };
    }

    private static Mock<ILlmProvider> Answering(string content)
    {
        var provider = new Mock<ILlmProvider>();
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = content });

        return provider;
    }

    // A provider whose call starts and ends only when the test finishes it, so its request keeps its slot until then.
    private static (Mock<ILlmProvider> Provider, TaskCompletionSource Started, TaskCompletionSource<LlmResponse> Finish) Held()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<LlmResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Mock<ILlmProvider>();
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                started.TrySetResult();
                return finish.Task;
            });

        return (provider, started, finish);
    }

    // A provider whose call starts and never ends, so its request keeps its slot.
    private static (Mock<ILlmProvider> Provider, TaskCompletionSource Started) Stalling()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<LlmResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Mock<ILlmProvider>();
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                started.TrySetResult();
                return never.Task;
            });

        return (provider, started);
    }

    /// <summary>A fake clock that reports each timer created on it, so a test advances time only once a wait has begun.</summary>
    internal sealed class TimerWatchingTimeProvider : FakeTimeProvider
    {
        private readonly Channel<TimeSpan> _created = Channel.CreateUnbounded<TimeSpan>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _created.Writer.TryWrite(dueTime);

            return timer;
        }

        /// <summary>Completes once a timer due after <paramref name="dueTime"/> has been created (now or earlier).</summary>
        public async Task WaitForTimerAsync(TimeSpan dueTime, TimeSpan guard)
        {
            using var timeout = new CancellationTokenSource(guard);
            var created = await _created.Reader.ReadAsync(timeout.Token);
            while (created != dueTime)
            {
                created = await _created.Reader.ReadAsync(timeout.Token);
            }
        }
    }
}
