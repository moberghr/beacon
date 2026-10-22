using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Models.Ai;
using Beacon.Tests.Common;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// The queue never retries an HTTP failure that carries a status: the provider that set it has applied its own retry
/// policy (408, 429 and 5xx, honouring Retry-After) and stopped. Failures without a status follow the queue's rules.
/// </summary>
[TestFixture]
public class LlmRequestQueueRetryTests
{
    [TestCase(HttpStatusCode.Unauthorized, "OpenAI returned HTTP 401 (invalid_api_key).")]
    [TestCase(HttpStatusCode.BadRequest, "OpenAI returned HTTP 400 (timeout).")]
    [TestCase(HttpStatusCode.NotFound, "Azure OpenAI returned HTTP 404.")]
    [TestCase(HttpStatusCode.RequestTimeout, "OpenAI returned HTTP 408.")]
    [TestCase(HttpStatusCode.TooManyRequests, "OpenAI returned HTTP 429 (rate_limit_exceeded).")]
    [TestCase(HttpStatusCode.InternalServerError, "OpenAI returned HTTP 500.")]
    [TestCase(HttpStatusCode.ServiceUnavailable, "Azure OpenAI returned HTTP 503.")]
    public async Task AnHttpFailureWithAStatus_IsNotRetried(HttpStatusCode status, string message)
    {
        var provider = new Mock<ILlmProvider>();
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(message, null, status));
        var queue = new LlmRequestQueue(1, 3);

        var act = () => queue.EnqueueRequestAsync(provider.Object, new LlmRequest());

        (await act.Should().ThrowExactlyAsync<HttpRequestException>()).Which.StatusCode.Should().Be(status);
        provider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AnHttpFailureWithoutAStatus_FollowsTheExistingRules_AndIsRetried()
    {
        var provider = new Mock<ILlmProvider>();
        provider
            .SetupSequence(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"))
            .ReturnsAsync(new LlmResponse { Content = "ok" });
        var queue = new LlmRequestQueue(1, 2);

        var response = await queue.EnqueueRequestAsync(provider.Object, new LlmRequest());

        response.Content.Should().Be("ok");
        provider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task AFailureWithoutAStatus_ThatMatchesNoRule_IsNotRetried()
    {
        var provider = new Mock<ILlmProvider>();
        provider
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bad request shape"));
        var queue = new LlmRequestQueue(1, 3);

        var act = () => queue.EnqueueRequestAsync(provider.Object, new LlmRequest());

        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
        provider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task ARetryAfterLongerThanTheCap_ThroughTheQueue_SendsOneRequest()
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));

            return response;
        });
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), TestCredentials.ProviderSecretKey, "m");
        var queue = new LlmRequestQueue(1, 3);

        var act = () => queue.EnqueueRequestAsync(provider, OpenAiCompatibleProviderTests.GoldenRequest())
            .WaitAsync(TestContext.CurrentContext.CancellationToken);

        (await act.Should().ThrowExactlyAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        handler.Requests.Should().ContainSingle();
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task AnUnauthorizedResponseWhoseBodyStalls_ThroughTheQueue_KeepsItsStatus_AndSendsOneRequest()
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StreamContent(new OpenAiCompatibleProviderTests.StalledStream()) });
        var provider = OpenAiCompatibleProvider.ForOpenAi(
            new HttpClient(handler),
            TestCredentials.ProviderSecretKey,
            "m",
            attemptTimeout: TimeSpan.FromMilliseconds(100));
        var queue = new LlmRequestQueue(1, 3);

        var act = () => queue.EnqueueRequestAsync(provider, OpenAiCompatibleProviderTests.GoldenRequest())
            .WaitAsync(TestContext.CurrentContext.CancellationToken);

        var thrown = await act.Should().ThrowExactlyAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        thrown.Which.Message.Should().Be("OpenAI returned HTTP 401.");
        handler.Requests.Should().ContainSingle();
    }
}
