using System.Net;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Models.Ai;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// The queue retries an HTTP failure only for a status that can pass on a retry (408, 429, 5xx): the provider has
/// retried it already, and an authentication or request error never passes.
/// </summary>
[TestFixture]
public class LlmRequestQueueRetryTests
{
    [TestCase(HttpStatusCode.Unauthorized, "OpenAI returned HTTP 401 (invalid_api_key).")]
    [TestCase(HttpStatusCode.BadRequest, "OpenAI returned HTTP 400 (timeout).")]
    [TestCase(HttpStatusCode.NotFound, "Azure OpenAI returned HTTP 404.")]
    public async Task AnHttpFailureThatCannotPass_IsNotRetried(HttpStatusCode status, string message)
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
    public async Task ServiceUnavailable_IsRetried()
    {
        var provider = new Mock<ILlmProvider>();
        provider
            .SetupSequence(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("OpenAI returned HTTP 503.", null, HttpStatusCode.ServiceUnavailable))
            .ReturnsAsync(new LlmResponse { Content = "ok" });
        var queue = new LlmRequestQueue(1, 2);

        var response = await queue.EnqueueRequestAsync(provider.Object, new LlmRequest());

        response.Content.Should().Be("ok");
        provider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // Each case waits through the queue's real first backoff (1 s), so the set stays small.
    [TestCase(HttpStatusCode.RequestTimeout)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task AStatusThatCanPass_IsRetried(HttpStatusCode status)
    {
        var provider = new Mock<ILlmProvider>();
        provider
            .SetupSequence(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("OpenAI returned HTTP n.", null, status))
            .ReturnsAsync(new LlmResponse { Content = "ok" });
        var queue = new LlmRequestQueue(1, 2);

        var response = await queue.EnqueueRequestAsync(provider.Object, new LlmRequest());

        response.Content.Should().Be("ok");
        provider.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
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
}
