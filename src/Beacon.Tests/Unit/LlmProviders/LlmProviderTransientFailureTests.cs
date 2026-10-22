using System.Net;
using System.Net.Http.Headers;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Exceptions;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// Claude and Bedrock do not retry themselves: a failure that may pass reaches the request queue as
/// <see cref="LlmTransientException"/> (with the wait the service asked for), every other failure as a plain
/// <see cref="AiServiceException"/> the queue does not retry.
/// </summary>
[TestFixture]
[CancelAfter(10_000)]
public class LlmProviderTransientFailureTests
{
    [Test]
    public async Task Claude_ARateLimitWithRetryAfter_IsTransient_AndCarriesTheWait()
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(
            HttpStatusCode.TooManyRequests,
            "{}",
            x => x.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7))));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-x");

        var act = () => provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        var thrown = await act.Should().ThrowExactlyAsync<LlmTransientException>();
        thrown.Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(7));
        thrown.Which.Message.Should().Be("Claude API returned HTTP 429.");
        handler.Requests.Should().ContainSingle("the provider leaves retrying to the queue");
    }

    [TestCase(HttpStatusCode.RequestTimeout)]
    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.BadGateway)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    [TestCase((HttpStatusCode)529)]
    public async Task Claude_AnOverloadOrServerFailure_IsTransient(HttpStatusCode status)
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(status, "{}"));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-x");

        var act = () => provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        (await act.Should().ThrowExactlyAsync<LlmTransientException>()).Which.RetryAfter.Should().BeNull();
    }

    [TestCase(HttpStatusCode.BadRequest)]
    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.NotFound)]
    public async Task Claude_ARequestFailure_IsNotTransient(HttpStatusCode status)
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(status, """{"error":{"message":"secret detail"}}"""));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-x");

        var act = () => provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        var thrown = await act.Should().ThrowExactlyAsync<AiServiceException>();
        thrown.Which.Message.Should().Be($"Claude API returned HTTP {(int)status}.");
    }

    [Test]
    public async Task Claude_AConnectionFailure_IsTransient()
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => throw new HttpRequestException("connection refused"));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-x");

        var act = () => provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        (await act.Should().ThrowExactlyAsync<LlmTransientException>()).Which.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Test]
    public async Task Claude_ARetryAfterDate_IsMeasuredAgainstTheProvidersClock()
    {
        var time = new FakeTimeProvider(Now);
        var future = await ClaudeFailure(time, HttpStatusCode.TooManyRequests, x => x.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddSeconds(30)));
        var past = await ClaudeFailure(time, HttpStatusCode.TooManyRequests, x => x.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddSeconds(-30)));
        var missing = await ClaudeFailure(time, HttpStatusCode.TooManyRequests, _ => { });

        future.RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
        past.RetryAfter.Should().Be(TimeSpan.Zero);
        missing.RetryAfter.Should().BeNull();
    }

    [TestCase(HttpStatusCode.ServiceUnavailable)]
    [TestCase((HttpStatusCode)529)]
    public async Task Claude_AnOverloadWithRetryAfter_CarriesTheWait(HttpStatusCode status)
    {
        var time = new FakeTimeProvider(Now);

        var delta = await ClaudeFailure(time, status, x => x.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5)));
        var date = await ClaudeFailure(time, status, x => x.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddSeconds(12)));

        delta.RetryAfter.Should().Be(TimeSpan.FromSeconds(5));
        date.RetryAfter.Should().Be(TimeSpan.FromSeconds(12));
    }

    [Test]
    public async Task Claude_TheClientsTimeout_WhileTheCallerWaits_IsTransient()
    {
        var handler = new OpenAiCompatibleProviderTests.StallingHandler();
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        var provider = new ClaudeProvider(client, TestCredentials.ProviderSecretKey, "claude-x");

        var act = () => provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        var thrown = await act.Should().ThrowExactlyAsync<LlmTransientException>();
        thrown.Which.Message.Should().Be("Claude API did not answer in time.");
        thrown.Which.InnerException.Should().BeAssignableTo<OperationCanceledException>();
    }

    [Test]
    public async Task Claude_TheCallersCancellation_IsACancellation_NotATransientFailure()
    {
        var provider = new ClaudeProvider(new HttpClient(new OpenAiCompatibleProviderTests.StallingHandler()), TestCredentials.ProviderSecretKey, "claude-x");
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest(), caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task Claude_AFailureCarryingAStatus_IsNotTransient_AndAConnectionResetIs()
    {
        var withStatus = new ClaudeProvider(
            new HttpClient(new OpenAiCompatibleProviderTests.StubHandler(_ => throw new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway))),
            TestCredentials.ProviderSecretKey,
            "claude-x");
        var reset = new ClaudeProvider(
            new HttpClient(new OpenAiCompatibleProviderTests.StubHandler(_ => throw new IOException("connection reset"))),
            TestCredentials.ProviderSecretKey,
            "claude-x");

        await withStatus.Invoking(x => x.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest())).Should().ThrowExactlyAsync<AiServiceException>();
        await reset.Invoking(x => x.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest())).Should().ThrowExactlyAsync<LlmTransientException>();
    }

    [Test]
    public async Task Claude_ARateLimit_ThroughTheQueue_IsRetriedAfterTheWait()
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(
            _ => OpenAiCompatibleProviderTests.Json(
                HttpStatusCode.TooManyRequests,
                "{}",
                x => x.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2))),
            _ => OpenAiCompatibleProviderTests.Json(
                HttpStatusCode.OK,
                """{"model":"claude-x","content":[{"type":"text","text":"hi"}],"usage":{"input_tokens":3,"output_tokens":4},"stop_reason":"end_turn"}"""));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-x");
        var time = new LlmRequestQueueSlotTests.TimerWatchingTimeProvider();
        var queue = new LlmRequestQueue(new ProviderLimits { MaxConcurrentRequests = 1 }, time, () => 0);

        var call = queue.EnqueueRequestAsync(provider, OpenAiCompatibleProviderTests.GoldenRequest());
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        call.IsCompleted.Should().BeFalse();
        handler.Requests.Should().ContainSingle();
        time.Advance(TimeSpan.FromSeconds(2));

        (await call.WaitAsync(TimeSpan.FromSeconds(5))).Content.Should().Be("hi");
        handler.Requests.Should().HaveCount(2);
    }

    [Test]
    public void Bedrock_TheSdkDoesNotRetry()
    {
        BedrockProvider.CreateConfig("eu-west-1").MaxErrorRetry.Should().Be(0);
    }

    [Test]
    public void Bedrock_ThrottlingAndUnavailability_AreTransient()
    {
        Exception[] transient =
        [
            new ThrottlingException("slow down"),
            new ServiceUnavailableException("unavailable"),
            new ModelTimeoutException("model timed out"),
            new InternalServerException("internal"),
            new HttpRequestException("connection refused")
        ];

        foreach (var failure in transient)
        {
            var mapped = BedrockProvider.TransientFailure(failure);

            mapped.Should().NotBeNull(failure.GetType().Name);
            mapped!.InnerException.Should().BeSameAs(failure);
            mapped.Message.Should().NotContain(failure.Message);
        }
    }

    [Test]
    public void Bedrock_RequestFailures_AreNotTransient()
    {
        BedrockProvider.TransientFailure(new ValidationException("bad input")).Should().BeNull();
        BedrockProvider.TransientFailure(new AccessDeniedException("denied")).Should().BeNull();
        BedrockProvider.TransientFailure(new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden)).Should().BeNull();
        BedrockProvider.TransientFailure(new FileNotFoundException("missing")).Should().BeNull();
    }

    [Test]
    public async Task Bedrock_ThroughCompleteAsync_AFailureThatMayPass_IsTransient(
        [ValueSource(nameof(BedrockTransientFailures))] Func<Exception> failure,
        [Values("anthropic.claude-3-haiku", "meta.llama3-1-8b")] string modelId)
    {
        var provider = Bedrock(modelId, failure());

        var thrown = await provider.Invoking(x => x.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest())).Should().ThrowExactlyAsync<LlmTransientException>();

        thrown.Which.RetryAfter.Should().BeNull();
        thrown.Which.Message.Should().NotContain(BedrockServiceText);
    }

    [Test]
    public async Task Bedrock_ThroughCompleteAsync_ARequestFailure_IsNotTransient(
        [ValueSource(nameof(BedrockRequestFailures))] Func<Exception> failure,
        [Values("anthropic.claude-3-haiku", "meta.llama3-1-8b")] string modelId)
    {
        var provider = Bedrock(modelId, failure());

        await provider.Invoking(x => x.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest())).Should().ThrowExactlyAsync<AiServiceException>();
    }

    [Test]
    public async Task Bedrock_ThroughCompleteAsync_TheCallersCancellation_IsACancellation()
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        var provider = Bedrock("anthropic.claude-3-haiku", new OperationCanceledException(caller.Token));

        await provider.Invoking(x => x.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest(), caller.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task Bedrock_ThroughTheQueue_AThrottledCall_IsTriedThreeTimes()
    {
        var client = new Mock<IAmazonBedrockRuntime>();
        client
            .Setup(x => x.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ThrottlingException(BedrockServiceText));
        var queue = new LlmRequestQueue(new ProviderLimits { MaxConcurrentRequests = 1 }, new FakeTimeProvider(), () => 0);

        var act = () => queue.EnqueueRequestAsync(new BedrockProvider(client.Object, "anthropic.claude-3-haiku"), OpenAiCompatibleProviderTests.GoldenRequest());

        await act.Should().ThrowExactlyAsync<LlmTransientException>();
        client.Verify(x => x.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    private const string BedrockServiceText = "SERVICE-TEXT-4d2c";

    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static IEnumerable<Func<Exception>> BedrockTransientFailures()
    {
        yield return () => new ThrottlingException(BedrockServiceText);
        yield return () => new ServiceUnavailableException(BedrockServiceText);
        yield return () => new ModelNotReadyException(BedrockServiceText);
        yield return () => new ModelTimeoutException(BedrockServiceText);
        yield return () => new InternalServerException(BedrockServiceText);
        yield return () => new AmazonBedrockRuntimeException(BedrockServiceText, ErrorType.Unknown, "x", "r", HttpStatusCode.RequestTimeout);
        yield return () => new AmazonBedrockRuntimeException(BedrockServiceText, ErrorType.Unknown, "x", "r", HttpStatusCode.TooManyRequests);
        yield return () => new AmazonBedrockRuntimeException(BedrockServiceText, ErrorType.Unknown, "x", "r", HttpStatusCode.BadGateway);
        yield return () => new AmazonBedrockRuntimeException(BedrockServiceText, ErrorType.Unknown, "x", "r", HttpStatusCode.GatewayTimeout);
        yield return () => new HttpRequestException(BedrockServiceText);
        yield return () => new IOException(BedrockServiceText);
        yield return () => new TimeoutException(BedrockServiceText);
        yield return () => new TaskCanceledException(BedrockServiceText);
    }

    private static IEnumerable<Func<Exception>> BedrockRequestFailures()
    {
        yield return () => new ValidationException(BedrockServiceText);
        yield return () => new AccessDeniedException(BedrockServiceText);
        yield return () => new AmazonBedrockRuntimeException(BedrockServiceText, ErrorType.Sender, "x", "r", HttpStatusCode.BadRequest);
        yield return () => new AmazonBedrockRuntimeException(BedrockServiceText, ErrorType.Sender, "x", "r", HttpStatusCode.Forbidden);
        yield return () => new HttpRequestException(BedrockServiceText, null, HttpStatusCode.Forbidden);
    }

    private static BedrockProvider Bedrock(string modelId, Exception failure)
    {
        var client = new Mock<IAmazonBedrockRuntime>();
        client
            .Setup(x => x.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        return new BedrockProvider(client.Object, modelId);
    }

    private static async Task<LlmTransientException> ClaudeFailure(TimeProvider time, HttpStatusCode status, Action<HttpResponseMessage> configure)
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(status, "{}", configure));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-x", time);

        var thrown = await provider.Invoking(x => x.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest())).Should().ThrowExactlyAsync<LlmTransientException>();

        return thrown.Which;
    }
}
