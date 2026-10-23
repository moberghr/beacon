using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Exceptions;
using Beacon.Core.Models.Ai;
using Beacon.Core.Data.Enums;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// The limits an Admin saves apply to the running queue: a new concurrency limit resizes the slot pools without cancelling
/// running attempts (the background share follows), and the requests and tokens per minute are enforced only while rate
/// limits are enabled, each as a per-minute token bucket whose wait stays within the lane's queue wait.
/// </summary>
[TestFixture]
[CancelAfter(10_000)]
public class LlmRequestQueueLimitTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    [Test]
    public async Task RaisingTheConcurrencyLimit_LetsAWaitingRequestRun()
    {
        var queue = CreateQueue(new FakeTimeProvider(), new ProviderLimits { MaxConcurrentRequests = 1 });
        _ = queue.EnqueueRequestAsync(Stalling().Provider.Object, new LlmRequest());
        var waiting = Stalling();
        _ = queue.EnqueueRequestAsync(waiting.Provider.Object, new LlmRequest());
        waiting.Started.Task.IsCompleted.Should().BeFalse();

        queue.UpdateLimits(new ProviderLimits { MaxConcurrentRequests = 2 });

        await waiting.Started.Task.WaitAsync(Guard);
    }

    [Test]
    public async Task LoweringTheConcurrencyLimit_LetsRunningAttemptsFinish_AndAdmitsWaitersOnlyBelowTheNewLimit()
    {
        var queue = CreateQueue(new FakeTimeProvider(), new ProviderLimits { MaxConcurrentRequests = 3 });
        var running = Enumerable.Range(0, 3).Select(_ => Held()).ToList();
        var runningTasks = running.Select(x => queue.EnqueueRequestAsync(x.Provider.Object, new LlmRequest())).ToList();
        foreach (var held in running)
        {
            await held.Started.Task.WaitAsync(Guard);
        }

        var waiters = new[] { Held(), Held() };
        foreach (var waiter in waiters)
        {
            _ = queue.EnqueueRequestAsync(waiter.Provider.Object, new LlmRequest());
        }

        queue.UpdateLimits(new ProviderLimits { MaxConcurrentRequests = 1 });

        running[0].Finish.SetResult(new LlmResponse { Content = "first" });
        running[1].Finish.SetResult(new LlmResponse { Content = "second" });
        (await runningTasks[0].WaitAsync(Guard)).Content.Should().Be("first", "a lower limit never cancels a running attempt");
        (await runningTasks[1].WaitAsync(Guard)).Content.Should().Be("second");
        waiters.Should().OnlyContain(x => !x.Started.Task.IsCompleted, "one attempt still runs, which is the new limit");

        running[2].Finish.SetResult(new LlmResponse { Content = "third" });
        (await runningTasks[2].WaitAsync(Guard)).Content.Should().Be("third");
        var admitted = await Task.WhenAny(waiters.Select(x => x.Started.Task)).WaitAsync(Guard);
        var other = waiters.Single(x => x.Started.Task != admitted);
        other.Started.Task.IsCompleted.Should().BeFalse("only one attempt runs under the new limit");

        waiters.Single(x => x.Started.Task == admitted).Finish.SetResult(new LlmResponse { Content = "waited" });
        await other.Started.Task.WaitAsync(Guard);
    }

    [Test]
    public async Task TheBackgroundShare_FollowsANewConcurrencyLimit()
    {
        var queue = CreateQueue(new FakeTimeProvider(), new ProviderLimits { MaxConcurrentRequests = 2 });
        _ = queue.EnqueueRequestAsync(Stalling().Provider.Object, Background());
        var secondBackground = Stalling();
        _ = queue.EnqueueRequestAsync(secondBackground.Provider.Object, Background());
        secondBackground.Started.Task.IsCompleted.Should().BeFalse("with two slots the background lane holds one");

        queue.UpdateLimits(new ProviderLimits { MaxConcurrentRequests = 4 });

        await secondBackground.Started.Task.WaitAsync(Guard);
        var interactive = Stalling();
        _ = queue.EnqueueRequestAsync(interactive.Provider.Object, new LlmRequest());
        await interactive.Started.Task.WaitAsync(Guard);
    }

    [Test]
    public async Task WithRateLimitsEnabled_ARequestPastTheRequestsPerMinute_WaitsForTheBucketToRefill()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, RateLimited(requestsPerMinute: 2, tokensPerMinute: 0));
        await queue.EnqueueRequestAsync(Answering().Object, new LlmRequest()).WaitAsync(Guard);
        await queue.EnqueueRequestAsync(Answering().Object, new LlmRequest()).WaitAsync(Guard);
        var third = Answering();

        var waiting = queue.EnqueueRequestAsync(third.Object, new LlmRequest());
        time.Advance(TimeSpan.FromSeconds(29));
        waiting.IsCompleted.Should().BeFalse("two requests a minute refill one every 30 s");
        third.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        time.Advance(TimeSpan.FromSeconds(1));

        (await waiting.WaitAsync(Guard)).Content.Should().Be("ok");
    }

    [Test]
    public async Task WithRateLimitsEnabled_TheTokenEstimateIsReserved_AndTheReportedUsageGivesTheRestBack()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, RateLimited(requestsPerMinute: 0, tokensPerMinute: 1000));
        var reporting = Answering(new LlmResponse { Content = "ok", InputTokens = 50, OutputTokens = 50 });

        await queue.EnqueueRequestAsync(reporting.Object, Estimated(600)).WaitAsync(Guard);
        var second = queue.EnqueueRequestAsync(Answering().Object, Estimated(600));

        second.IsCompleted.Should().BeTrue("the first request used 100 of the 600 tokens it reserved, which leaves 900");
        (await second).Content.Should().Be("ok");
    }

    [Test]
    public async Task WithRateLimitsEnabled_AResponseWithoutUsage_KeepsTheEstimate_SoTheNextRequestWaits()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, RateLimited(requestsPerMinute: 0, tokensPerMinute: 1000));

        await queue.EnqueueRequestAsync(Answering().Object, Estimated(600)).WaitAsync(Guard);
        var second = queue.EnqueueRequestAsync(Answering().Object, Estimated(600));
        time.Advance(TimeSpan.FromSeconds(11));
        second.IsCompleted.Should().BeFalse("400 tokens are left and 200 more refill in 12 s");
        time.Advance(TimeSpan.FromSeconds(1));

        (await second.WaitAsync(Guard)).Content.Should().Be("ok");
    }

    [Test]
    public void TheTokenEstimate_IsTheInputCharactersOverFour_PlusTheMaxTokens()
    {
        var request = new LlmRequest
        {
            SystemPrompt = new string('s', 40),
            Messages = [new ChatMessage(ConversationRole.User, new string('u', 361))],
            MaxTokens = 500
        };

        LlmRequestQueue.EstimateTokens(request).Should().Be(601);
    }

    [Test]
    public async Task WithRateLimitsEnabled_ARequestThatCannotGetItsTokensWithinTheWaitLimit_FailsAsBusy_WithoutReachingTheProvider()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, RateLimited(requestsPerMinute: 0, tokensPerMinute: 1000) with { InteractiveQueueWaitSeconds = 30 });
        await queue.EnqueueRequestAsync(Answering().Object, Estimated(1000)).WaitAsync(Guard);
        var refused = Answering();

        var act = () => queue.EnqueueRequestAsync(refused.Object, Estimated(1000)).WaitAsync(Guard);

        await act.Should().ThrowExactlyAsync<LlmQueueSaturatedException>();
        refused.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task WithRateLimitsDisabled_TheStoredRatesAreNotApplied()
    {
        var queue = CreateQueue(new FakeTimeProvider(), new ProviderLimits { RequestsPerMinute = 1, TokensPerMinute = 1 });

        for (var i = 0; i < 3; i++)
        {
            var response = queue.EnqueueRequestAsync(Answering().Object, Estimated(1000));
            response.IsCompleted.Should().BeTrue();
            (await response).Content.Should().Be("ok");
        }
    }

    [TestCase(0, 1_000_000)]
    [TestCase(-1, 1_000_000)]
    [TestCase(1_000, 0)]
    [TestCase(1_000, -5)]
    public async Task WithRateLimitsEnabled_AZeroOrNegativeRate_IsUnlimited(int requestsPerMinute, int tokensPerMinute)
    {
        var queue = CreateQueue(new FakeTimeProvider(), RateLimited(requestsPerMinute, tokensPerMinute));

        for (var i = 0; i < 5; i++)
        {
            var response = queue.EnqueueRequestAsync(Answering().Object, Estimated(100_000));
            response.IsCompleted.Should().BeTrue();
            (await response).Content.Should().Be("ok");
        }
    }

    [Test]
    public async Task EnablingRateLimits_AppliesToTheNextRequest_AndDisablingThem_LetsTheNextOneThroughAtOnce()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, new ProviderLimits { RequestsPerMinute = 1, InteractiveQueueWaitSeconds = 120 });
        await queue.EnqueueRequestAsync(Answering().Object, new LlmRequest()).WaitAsync(Guard);

        queue.UpdateLimits(RateLimited(requestsPerMinute: 1, tokensPerMinute: 0));
        await queue.EnqueueRequestAsync(Answering().Object, new LlmRequest()).WaitAsync(Guard);
        var limited = queue.EnqueueRequestAsync(Answering().Object, new LlmRequest());
        limited.IsCompleted.Should().BeFalse("one request a minute is enforced once enabled");

        queue.UpdateLimits(new ProviderLimits { RequestsPerMinute = 1 });
        var unlimited = queue.EnqueueRequestAsync(Answering().Object, new LlmRequest());

        unlimited.IsCompleted.Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(60));
        (await limited.WaitAsync(Guard)).Content.Should().Be("ok");
    }

    [Test]
    public async Task WithRateLimitsEnabled_AResponseWithoutInputTokens_KeepsTheEstimate()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, RateLimited(requestsPerMinute: 0, tokensPerMinute: 1000));
        var outputOnly = Answering(new LlmResponse { Content = "ok", OutputTokens = 50 });

        await queue.EnqueueRequestAsync(outputOnly.Object, Estimated(600)).WaitAsync(Guard);
        var second = queue.EnqueueRequestAsync(Answering().Object, Estimated(600));

        second.IsCompleted.Should().BeFalse("without the input tokens the reported usage is short, so the 600 reserved stand");
        time.Advance(TimeSpan.FromSeconds(12));
        (await second.WaitAsync(Guard)).Content.Should().Be("ok");
    }

    [Test]
    public async Task WithRateLimitsEnabled_ARequestRefusedForTooManyWaiters_GivesItsReservationBack()
    {
        var queue = CreateQueue(new FakeTimeProvider(), RateLimited(requestsPerMinute: 3, tokensPerMinute: 0) with { MaxConcurrentRequests = 1, MaxQueuedRequests = 1 });
        var running = Held();
        _ = queue.EnqueueRequestAsync(running.Provider.Object, new LlmRequest());
        await running.Started.Task.WaitAsync(Guard);
        var waiting = Held();
        var waitingTask = queue.EnqueueRequestAsync(waiting.Provider.Object, new LlmRequest());

        var refused = () => queue.EnqueueRequestAsync(Answering().Object, new LlmRequest());

        await refused.Should().ThrowExactlyAsync<LlmQueueSaturatedException>();
        running.Finish.SetResult(new LlmResponse { Content = "first" });
        await waiting.Started.Task.WaitAsync(Guard);
        waiting.Finish.SetResult(new LlmResponse { Content = "second" });
        await waitingTask.WaitAsync(Guard);
        await AdmittedAtOnce(queue);
    }

    [Test]
    public async Task WithRateLimitsEnabled_ACallerCancellingWhileWaitingForTheRate_LeavesTheLine()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, RateLimited(requestsPerMinute: 1, tokensPerMinute: 0) with { InteractiveQueueWaitSeconds = 300 });
        await queue.EnqueueRequestAsync(Answering().Object, new LlmRequest()).WaitAsync(Guard);
        using var cancel = new CancellationTokenSource();
        var cancelled = queue.EnqueueRequestAsync(Answering().Object, new LlmRequest(), cancel.Token);

        await cancel.CancelAsync();

        var act = () => cancelled.WaitAsync(Guard);
        await act.Should().ThrowAsync<OperationCanceledException>();
        var next = queue.EnqueueRequestAsync(Answering().Object, new LlmRequest());
        time.Advance(TimeSpan.FromSeconds(60));
        (await next.WaitAsync(Guard)).Content.Should().Be("ok", "the cancelled request no longer holds up the line");
    }

    [Test]
    public async Task WithRateLimitsEnabled_ACallerCancellingWhileWaitingForASlot_GivesItsReservationBack()
    {
        var queue = CreateQueue(new FakeTimeProvider(), RateLimited(requestsPerMinute: 2, tokensPerMinute: 0) with { MaxConcurrentRequests = 1 });
        var running = Held();
        var runningTask = queue.EnqueueRequestAsync(running.Provider.Object, new LlmRequest());
        await running.Started.Task.WaitAsync(Guard);
        using var cancel = new CancellationTokenSource();
        var cancelled = queue.EnqueueRequestAsync(Answering().Object, new LlmRequest(), cancel.Token);

        await cancel.CancelAsync();

        var act = () => cancelled.WaitAsync(Guard);
        await act.Should().ThrowAsync<OperationCanceledException>();
        running.Finish.SetResult(new LlmResponse { Content = "done" });
        await runningTask.WaitAsync(Guard);
        await AdmittedAtOnce(queue);
    }

    [Test]
    public async Task WithRateLimitsEnabled_ARequestTimingOutWhileWaitingForASlot_GivesItsReservationBack()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, RateLimited(requestsPerMinute: 2, tokensPerMinute: 0) with { MaxConcurrentRequests = 1, InteractiveQueueWaitSeconds = 5 });
        var running = Held();
        var runningTask = queue.EnqueueRequestAsync(running.Provider.Object, new LlmRequest());
        await running.Started.Task.WaitAsync(Guard);
        var timedOut = queue.EnqueueRequestAsync(Answering().Object, new LlmRequest());

        time.Advance(TimeSpan.FromSeconds(5));

        var act = () => timedOut.WaitAsync(Guard);
        await act.Should().ThrowExactlyAsync<LlmQueueSaturatedException>();
        running.Finish.SetResult(new LlmResponse { Content = "done" });
        await runningTask.WaitAsync(Guard);
        await AdmittedAtOnce(queue);
    }

    [Test]
    public async Task WithRateLimitsEnabled_ABackgroundRequestCancelledWhileWaitingForAMainSlot_ReturnsItsBackgroundSlotAndReservation()
    {
        var queue = CreateQueue(new FakeTimeProvider(), RateLimited(requestsPerMinute: 4, tokensPerMinute: 0) with { MaxConcurrentRequests = 2 });
        var interactive = new[] { Held(), Held() };
        var interactiveTasks = interactive.Select(x => queue.EnqueueRequestAsync(x.Provider.Object, new LlmRequest())).ToList();
        foreach (var held in interactive)
        {
            await held.Started.Task.WaitAsync(Guard);
        }

        using var cancel = new CancellationTokenSource();
        var cancelled = queue.EnqueueRequestAsync(Answering().Object, Background(), cancel.Token);
        await cancel.CancelAsync();

        var act = () => cancelled.WaitAsync(Guard);
        await act.Should().ThrowAsync<OperationCanceledException>();
        interactive[0].Finish.SetResult(new LlmResponse { Content = "done" });
        await interactiveTasks[0].WaitAsync(Guard);
        var background = queue.EnqueueRequestAsync(Answering().Object, Background());
        background.IsCompleted.Should().BeTrue("the background slot and the two requests a background request needs (one plus the reserve) are back");
    }

    [Test]
    public async Task WithRateLimitsEnabled_ALargeWaitingRequest_IsServedBeforeASmallerLaterOne()
    {
        var time = new FakeTimeProvider();
        var queue = CreateQueue(time, RateLimited(requestsPerMinute: 0, tokensPerMinute: 600) with { InteractiveQueueWaitSeconds = 120 });
        await queue.EnqueueRequestAsync(Answering().Object, Estimated(500)).WaitAsync(Guard);
        var largeProvider = Answering();
        var large = queue.EnqueueRequestAsync(largeProvider.Object, Estimated(400));
        time.Advance(TimeSpan.FromSeconds(10));

        var small = queue.EnqueueRequestAsync(Answering().Object, Estimated(50));

        small.IsCompleted.Should().BeFalse("200 tokens would cover it, but the larger request came first");
        time.Advance(TimeSpan.FromSeconds(20));
        (await large.WaitAsync(Guard)).Content.Should().Be("ok");
        for (var i = 0; i < 30 && !small.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        (await small.WaitAsync(Guard)).Content.Should().Be("ok");
    }

    // The next interactive request is admitted without waiting: a slot is free and the rate buckets cover it.
    private static async Task AdmittedAtOnce(LlmRequestQueue queue)
    {
        var next = queue.EnqueueRequestAsync(Answering().Object, new LlmRequest());

        next.IsCompleted.Should().BeTrue("the reservation the refused request took was given back");
        (await next).Content.Should().Be("ok");
    }

    private static LlmRequestQueue CreateQueue(TimeProvider time, ProviderLimits limits)
    {
        return new LlmRequestQueue(limits, time, () => 0);
    }

    private static ProviderLimits RateLimited(int requestsPerMinute, int tokensPerMinute)
    {
        return new ProviderLimits
        {
            RateLimitsEnabled = true,
            RequestsPerMinute = requestsPerMinute,
            TokensPerMinute = tokensPerMinute
        };
    }

    // A request whose token estimate is exactly maxTokens: no input text.
    private static LlmRequest Estimated(int maxTokens)
    {
        return new LlmRequest { MaxTokens = maxTokens };
    }

    private static LlmRequest Background()
    {
        return new LlmRequest { Priority = LlmRequestPriority.Background };
    }

    private static Mock<ILlmProvider> Answering(LlmResponse? response = null)
    {
        var provider = new Mock<ILlmProvider>();
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response ?? new LlmResponse { Content = "ok" });

        return provider;
    }

    // A provider whose call starts and ends only when the test finishes it, so its request keeps its slot until then.
    private static HeldCall Held()
    {
        var held = new HeldCall();
        held.Provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                held.Started.TrySetResult();
                return held.Finish.Task;
            });

        return held;
    }

    // A provider whose call starts and never ends, so its request keeps its slot.
    private static (Mock<ILlmProvider> Provider, TaskCompletionSource Started) Stalling()
    {
        var held = Held();

        return (held.Provider, held.Started);
    }

    private sealed class HeldCall
    {
        public Mock<ILlmProvider> Provider { get; } = new();

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<LlmResponse> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
