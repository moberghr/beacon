using System.Globalization;
using System.Net;
using Amazon.Runtime;
using Beacon.AI.Models.Configuration;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Exceptions;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// Bedrock through the real AWS SDK pipeline (only the HTTP handler is a stub): the SDK retries no failure of its own
/// except a request refused for its signature time while the service's clock is far from the local one, which it
/// re-signs with the service's time and sends once more. Each test uses its own region, because the SDK keeps the
/// clock correction it learns per endpoint for the whole process.
/// </summary>
[TestFixture]
[CancelAfter(10_000)]
public class BedrockClockSkewTests
{
    [Test]
    public async Task ASignatureTimeRefusal_WhileTheServiceClockIsAnHourAhead_IsSentOnceMore_SignedWithTheServiceTime()
    {
        var serviceTime = DateTimeOffset.UtcNow.AddHours(1);
        var handler = new OpenAiCompatibleProviderTests.StubHandler(
            _ => Failure(HttpStatusCode.Forbidden, "InvalidSignatureException", serviceTime),
            _ => Success(serviceTime));
        var provider = Provider("ap-southeast-4", handler);

        var response = await provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        response.Content.Should().Be("hello");
        handler.Requests.Should().HaveCount(2);
        SignedAt(handler.Requests[1]).Should().BeCloseTo(serviceTime, TimeSpan.FromMinutes(1), "the second attempt is signed with the service's time");
    }

    [Test]
    public async Task ASignatureTimeRefusal_ThatPersists_IsSentTwice_AndIsNotTransient()
    {
        var serviceTime = DateTimeOffset.UtcNow.AddHours(-1);
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => Failure(HttpStatusCode.Forbidden, "InvalidSignatureException", serviceTime));
        var provider = Provider("ap-southeast-5", handler);

        await provider.Invoking(x => x.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest())).Should().ThrowExactlyAsync<AiServiceException>();

        handler.Requests.Should().HaveCount(2);
    }

    [TestCase("AccessDeniedException")]
    [TestCase("UnrecognizedClientException")]
    [TestCase("InvalidSignatureException")]
    public async Task AnAuthFailure_WhileTheClocksAgree_IsSentOnce_AndIsNotTransient(string errorType)
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => Failure(HttpStatusCode.Forbidden, errorType, DateTimeOffset.UtcNow));
        var provider = Provider("ca-west-1", handler);

        await provider.Invoking(x => x.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest())).Should().ThrowExactlyAsync<AiServiceException>();

        handler.Requests.Should().ContainSingle();
    }

    [TestCase(HttpStatusCode.TooManyRequests, "ThrottlingException")]
    [TestCase(HttpStatusCode.ServiceUnavailable, "ServiceUnavailableException")]
    [TestCase(HttpStatusCode.InternalServerError, "InternalServerException")]
    public async Task AFailureThatMayPass_IsSentOncePerQueueAttempt(HttpStatusCode status, string errorType)
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => Failure(status, errorType, DateTimeOffset.UtcNow));
        var queue = new LlmRequestQueue(new ProviderLimits { MaxConcurrentRequests = 1 }, new FakeTimeProvider(), () => 0);

        var act = () => queue.EnqueueRequestAsync(Provider("mx-central-1", handler), OpenAiCompatibleProviderTests.GoldenRequest());

        await act.Should().ThrowExactlyAsync<LlmTransientException>();
        handler.Requests.Should().HaveCount(3, "the queue tries three times and the SDK adds no attempt of its own");
    }

    private static BedrockProvider Provider(string region, HttpMessageHandler handler)
    {
        var config = BedrockProvider.CreateConfig(region);
        config.HttpClientFactory = new StubHttpClientFactory(handler);
        var credentials = new BasicAWSCredentials(TestCredentials.AwsAccessKeyId, TestCredentials.AwsSecretAccessKey);

        return new BedrockProvider(BedrockProvider.CreateClient(credentials, config), "anthropic.claude-3-haiku");
    }

    private static HttpResponseMessage Failure(HttpStatusCode status, string errorType, DateTimeOffset serviceTime)
    {
        return OpenAiCompatibleProviderTests.Json(status, """{"message":"refused"}""", x =>
        {
            x.Headers.Add("x-amzn-ErrorType", errorType);
            x.Headers.Add("x-amzn-RequestId", "request-1");
            x.Headers.Date = serviceTime;
        });
    }

    private static HttpResponseMessage Success(DateTimeOffset serviceTime)
    {
        return OpenAiCompatibleProviderTests.Json(
            HttpStatusCode.OK,
            """{"content":[{"type":"text","text":"hello"}],"usage":{"input_tokens":1,"output_tokens":1},"stop_reason":"end_turn"}""",
            x => x.Headers.Date = serviceTime);
    }

    private static DateTimeOffset SignedAt(OpenAiCompatibleProviderTests.SentRequest request)
    {
        return DateTimeOffset.ParseExact(request.Headers["X-Amz-Date"], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            return new HttpClient(handler, disposeHandler: false);
        }

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig)
        {
            return false;
        }
    }
}
