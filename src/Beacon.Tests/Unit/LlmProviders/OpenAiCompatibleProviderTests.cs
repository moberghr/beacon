using System.Net;
using System.Text;
using System.Text.Json;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data.Enums;
using Beacon.Core.Exceptions;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// The raw-HTTP OpenAI / Azure OpenAI provider: the request matches what the vendor SDKs sent (goldens recorded from
/// OpenAI 2.14.0 and Azure.AI.OpenAI 2.1.0), the response maps as before, failures carry the status but never the
/// body or the key, and retries follow the SDK default policy.
/// </summary>
[TestFixture]
public class OpenAiCompatibleProviderTests
{
    private const string Key = TestCredentials.ProviderSecretKey;
    private const string BodyMarker = "BODY-MARKER-7f3a";
    private const string Completion = """{"id":"x","object":"chat.completion","model":"gpt-4o-mini-2024","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2000000,"completion_tokens":1000000,"total_tokens":3000000}}""";

    [Test]
    public async Task OpenAi_SendsTheRequestTheSdkSent()
    {
        var golden = Golden("openai-chat-completions.json");
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "gpt-4o-mini");

        await provider.CompleteAsync(GoldenRequest());

        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(golden.GetProperty("method").GetString());
        sent.Uri.Should().Be(golden.GetProperty("url").GetString());
        sent.Headers.Should().Contain("Authorization", $"Bearer {Key}").And.NotContainKey("api-key");
        sent.ContentType.Should().Be("application/json");
        JsonElement.DeepEquals(Parse(sent.Body), golden.GetProperty("body")).Should().BeTrue("the body was {0}", sent.Body);
    }

    [Test]
    public async Task Azure_SendsTheRequestTheSdkSent_AtThePinnedApiVersion()
    {
        var golden = Golden("azure-openai-chat-completions.json");
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForAzure(
            new HttpClient(handler),
            golden.GetProperty("endpoint").GetString()!,
            Key,
            golden.GetProperty("deployment").GetString()!);

        await provider.CompleteAsync(GoldenRequest());

        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(golden.GetProperty("method").GetString());
        sent.Uri.Should().Be(golden.GetProperty("url").GetString()).And.EndWith("?api-version=2024-10-21");
        sent.Headers.Should().Contain("api-key", Key).And.NotContainKey("Authorization");
        JsonElement.DeepEquals(Parse(sent.Body), golden.GetProperty("body")).Should().BeTrue("the body was {0}", sent.Body);
    }

    [TestCase("https://beacon-test.openai.azure.com", "https://beacon-test.openai.azure.com/openai/deployments/dep/chat/completions?api-version=2024-10-21")]
    [TestCase("https://beacon-test.openai.azure.com/", "https://beacon-test.openai.azure.com/openai/deployments/dep/chat/completions?api-version=2024-10-21")]
    [TestCase("https://beacon-test.openai.azure.com/sub/path", "https://beacon-test.openai.azure.com/sub/path/openai/deployments/dep/chat/completions?api-version=2024-10-21")]
    [TestCase("https://beacon-test.openai.azure.com/sub/path/", "https://beacon-test.openai.azure.com/sub/path/openai/deployments/dep/chat/completions?api-version=2024-10-21")]
    [TestCase("https://beacon-test.openai.azure.com/?x=1", "https://beacon-test.openai.azure.com/openai/deployments/dep/chat/completions?x=1&api-version=2024-10-21")]
    [TestCase("http://localhost:8080/", "http://localhost:8080/openai/deployments/dep/chat/completions?api-version=2024-10-21")]
    public void Azure_BuildsTheUrlTheSdkBuilt(string endpoint, string expected)
    {
        var provider = OpenAiCompatibleProvider.ForAzure(new HttpClient(), endpoint, Key, "dep");

        provider.RequestUri.AbsoluteUri.Should().Be(expected);
    }

    [TestCase("a b", "a%20b")]
    [TestCase("a/b", "a%2Fb")]
    [TestCase("a?b#c", "a%3Fb%23c")]
    [TestCase("ä%", "%C3%A4%25")]
    public void Azure_EscapesTheDeploymentLikeTheSdk(string deployment, string escaped)
    {
        var provider = OpenAiCompatibleProvider.ForAzure(new HttpClient(), "https://h.example/", Key, deployment);

        provider.RequestUri.AbsoluteUri.Should().Be($"https://h.example/openai/deployments/{escaped}/chat/completions?api-version=2024-10-21");
        provider.Model.Should().Be(deployment);
    }

    [Test]
    public void Azure_RefusesAnEndpointThatIsNotAnAbsoluteHttpUri()
    {
        var relative = () => OpenAiCompatibleProvider.ForAzure(new HttpClient(), "beacon-test.openai.azure.com", Key, "dep");
        var ftp = () => OpenAiCompatibleProvider.ForAzure(new HttpClient(), "ftp://beacon-test.openai.azure.com/", Key, "dep");

        relative.Should().Throw<UriFormatException>();
        ftp.Should().Throw<ArgumentException>().WithMessage("The Azure OpenAI endpoint must be an absolute http or https URI.*");
    }

    [Test]
    public async Task Completion_MapsContentUsageCostAndTheConfiguredModel()
    {
        var provider = OpenAi(_ => Json(HttpStatusCode.OK, Completion), model: "gpt-4-turbo");

        var response = await provider.CompleteAsync(GoldenRequest());

        response.Content.Should().Be("hello");
        response.InputTokens.Should().Be(2_000_000);
        response.OutputTokens.Should().Be(1_000_000);
        response.Cost.Should().Be(50.00m, "2M input at 10 and 1M output at 30 per million");
        response.Model.Should().Be("gpt-4-turbo", "the configured model, not the one the service reports");
        response.Truncated.Should().BeFalse();
        response.PromptCacheHit.Should().BeFalse();
    }

    [Test]
    public async Task Completion_OnAzure_UsesTheFlatRate()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForAzure(new HttpClient(handler), "https://h.example/", Key, "gpt-4o-mini");

        var response = await provider.CompleteAsync(GoldenRequest());

        response.Cost.Should().Be(25.00m, "2M input at 5 and 1M output at 15 per million");
        response.Model.Should().Be("gpt-4o-mini");
    }

    [TestCase("""{"role":"assistant","content":null,"refusal":"I can't help with that."}""", "")]
    [TestCase("""{"role":"assistant"}""", "")]
    [TestCase("""{"role":"assistant","content":[{"type":"text","text":"part1"},{"type":"text","text":"part2"}]}""", "part1")]
    [TestCase("""{"role":"assistant","content":[]}""", "")]
    public async Task Completion_ReadsTheContentLikeTheSdk(string message, string expected)
    {
        var body = $$$"""{"choices":[{"index":0,"message":{{{message}}},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2}}""";
        var provider = OpenAi(_ => Json(HttpStatusCode.OK, body));

        var response = await provider.CompleteAsync(GoldenRequest());

        response.Content.Should().Be(expected);
        response.InputTokens.Should().Be(1);
        response.OutputTokens.Should().Be(2);
    }

    [TestCase("length", true)]
    [TestCase("stop", false)]
    [TestCase("content_filter", false)]
    public async Task Completion_IsTruncatedOnlyWhenTheFinishReasonIsLength(string finishReason, bool truncated)
    {
        var body = $$$"""{"choices":[{"index":0,"message":{"role":"assistant","content":"x"},"finish_reason":"{{{finishReason}}}"}],"usage":{"prompt_tokens":1,"completion_tokens":2}}""";
        var provider = OpenAi(_ => Json(HttpStatusCode.OK, body));

        var response = await provider.CompleteAsync(GoldenRequest());

        response.Truncated.Should().Be(truncated);
    }

    [TestCase("""{"choices":[{"index":0,"message":{"role":"assistant","content":"x"},"finish_reason":"stop"}]}""", "OpenAI response did not contain usage information.")]
    [TestCase("""{"choices":[],"usage":{"prompt_tokens":1,"completion_tokens":2}}""", "OpenAI response did not contain a completion.")]
    [TestCase("""not json BODY-MARKER-7f3a""", "OpenAI returned a response that could not be read.")]
    [TestCase("""["BODY-MARKER-7f3a"]""", "OpenAI response did not contain a completion.")]
    public async Task Completion_ThatCannotBeMapped_FailsWithAValueFreeMessage(string body, string message)
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, body));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m");

        var act = () => provider.CompleteAsync(GoldenRequest());

        var thrown = await act.Should().ThrowExactlyAsync<AiServiceException>().WithMessage(message);
        thrown.Which.InnerException.Should().BeNull();
        handler.Requests.Should().ContainSingle("a 2xx is never retried");
    }

    [Test]
    public async Task Completion_LargerThanTheCap_Fails()
    {
        var provider = OpenAi(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[OpenAiCompatibleProvider.MaxResponseBytes + 1])
        });

        var act = () => provider.CompleteAsync(GoldenRequest());

        await act.Should().ThrowExactlyAsync<AiServiceException>().WithMessage("OpenAI returned a response larger than *");
    }

    [Test]
    public async Task Unauthorized_FailsWithTheStatusAndTheErrorCode_NeverTheBodyOrTheKey()
    {
        var handler = new StubHandler(_ => Json(
            HttpStatusCode.Unauthorized,
            $$$"""{"error":{"message":"Incorrect API key provided: {{{Key}}}. {{{BodyMarker}}}","type":"invalid_request_error","code":"invalid_api_key"}}"""));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder([]));

        var act = () => provider.CompleteAsync(GoldenRequest());

        var thrown = await act.Should().ThrowExactlyAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        thrown.Which.Message.Should().Be("OpenAI returned HTTP 401 (invalid_api_key).");
        thrown.Which.ToString().Should().NotContain(BodyMarker).And.NotContain(Key).And.NotContain("Count tables");
        handler.Requests.Should().ContainSingle("401 is not retried");
    }

    [TestCase("""{"error":{"code":"Invalid Key BODY-MARKER-7f3a","message":"x"}}""")]
    [TestCase("""{"error":{"code":"DeploymentNotFound","message":"x"}}""")]
    [TestCase("""{"error":{"code":401,"message":"x"}}""")]
    [TestCase("""BODY-MARKER-7f3a""")]
    [TestCase("")]
    public async Task Failure_NamesOnlyAnErrorCodeThatIsAPlainToken(string body)
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.NotFound, body));
        var provider = OpenAiCompatibleProvider.ForAzure(new HttpClient(handler), "https://h.example/", Key, "dep");

        var act = () => provider.CompleteAsync(GoldenRequest());

        var thrown = await act.Should().ThrowExactlyAsync<HttpRequestException>();
        thrown.Which.Message.Should().Be("Azure OpenAI returned HTTP 404.");
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task TooManyRequests_WithRetryAfterSeconds_WaitsAsAskedAndRetries()
    {
        var delays = new List<TimeSpan>();
        var handler = new StubHandler(
            _ => Json(HttpStatusCode.TooManyRequests, "{}", x => x.Headers.TryAddWithoutValidation("Retry-After", "1")),
            _ => Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder(delays));

        var response = await provider.CompleteAsync(GoldenRequest());

        response.Content.Should().Be("hello");
        delays.Should().Equal(TimeSpan.FromSeconds(1));
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Body.Should().Be(handler.Requests[0].Body, "a retry sends the same request");
        handler.Requests[1].Headers.Should().Contain("Authorization", $"Bearer {Key}");
    }

    [Test]
    public async Task RetryAfterMs_TakesPrecedence()
    {
        var delays = new List<TimeSpan>();
        var handler = new StubHandler(
            _ => Json(HttpStatusCode.ServiceUnavailable, "{}", x =>
            {
                x.Headers.TryAddWithoutValidation("retry-after-ms", "250");
                x.Headers.TryAddWithoutValidation("Retry-After", "7");
            }),
            _ => Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder(delays));

        await provider.CompleteAsync(GoldenRequest());

        delays.Should().Equal(TimeSpan.FromMilliseconds(250));
    }

    [Test]
    public async Task RetryAfterAsAnHttpDate_WaitsUntilThen()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        var delays = new List<TimeSpan>();
        var handler = new StubHandler(
            _ => Json(HttpStatusCode.TooManyRequests, "{}", x => x.Headers.TryAddWithoutValidation("Retry-After", "Wed, 21 Oct 2026 12:00:05 GMT")),
            _ => Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder(delays), time);

        await provider.CompleteAsync(GoldenRequest());

        delays.Should().Equal(TimeSpan.FromSeconds(5));
    }

    [TestCase("120")]
    [TestCase("61")]
    public async Task RetryAfterLongerThanTheCap_IsNotRetried(string retryAfter)
    {
        var delays = new List<TimeSpan>();
        var handler = new StubHandler(_ => Json(HttpStatusCode.TooManyRequests, "{}", x => x.Headers.TryAddWithoutValidation("Retry-After", retryAfter)));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder(delays));

        var act = () => provider.CompleteAsync(GoldenRequest());

        (await act.Should().ThrowExactlyAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        delays.Should().BeEmpty();
        handler.Requests.Should().ContainSingle();
    }

    [Test]
    public async Task ServerError_IsRetriedThreeTimesWithBackoff_ThenFails()
    {
        var delays = new List<TimeSpan>();
        var handler = new StubHandler(_ => Json(HttpStatusCode.InternalServerError, BodyMarker));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder(delays));

        var act = () => provider.CompleteAsync(GoldenRequest());

        var thrown = await act.Should().ThrowExactlyAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        thrown.Which.Message.Should().Be("OpenAI returned HTTP 500.");
        handler.Requests.Should().HaveCount(4);
        delays.Should().HaveCount(3);
        delays[0].Should().BeCloseTo(TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(161));
        delays[1].Should().BeCloseTo(TimeSpan.FromMilliseconds(1600), TimeSpan.FromMilliseconds(321));
        delays[2].Should().BeCloseTo(TimeSpan.FromMilliseconds(3200), TimeSpan.FromMilliseconds(641));
    }

    [TestCase(HttpStatusCode.RequestTimeout, true)]
    [TestCase(HttpStatusCode.TooManyRequests, true)]
    [TestCase(HttpStatusCode.BadGateway, true)]
    [TestCase(HttpStatusCode.ServiceUnavailable, true)]
    [TestCase(HttpStatusCode.GatewayTimeout, true)]
    [TestCase(HttpStatusCode.BadRequest, false)]
    [TestCase(HttpStatusCode.Forbidden, false)]
    [TestCase(HttpStatusCode.NotFound, false)]
    [TestCase(HttpStatusCode.NotImplemented, false)]
    public async Task OnlyTransientStatuses_AreRetried(HttpStatusCode status, bool retried)
    {
        var handler = new StubHandler(_ => Json(status, "{}"), _ => Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder([]));

        var act = () => provider.CompleteAsync(GoldenRequest());

        if (retried)
        {
            await act.Should().NotThrowAsync();
            handler.Requests.Should().HaveCount(2);
        }
        else
        {
            (await act.Should().ThrowExactlyAsync<HttpRequestException>()).Which.StatusCode.Should().Be(status);
            handler.Requests.Should().ContainSingle();
        }
    }

    [Test]
    public async Task TransportFailures_AreRetried()
    {
        var delays = new List<TimeSpan>();
        var handler = new StubHandler(
            _ => throw new HttpRequestException("connection refused"),
            _ => throw new IOException("connection reset"),
            _ => Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder(delays));

        var response = await provider.CompleteAsync(GoldenRequest());

        response.Content.Should().Be("hello");
        delays.Should().HaveCount(2);
    }

    [Test]
    public async Task Cancellation_BeforeSending_SendsNothing()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => provider.CompleteAsync(GoldenRequest(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().BeEmpty();
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Cancellation_DuringARetryDelay_StopsWaiting()
    {
        var delayEntered = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(_ => Json(HttpStatusCode.TooManyRequests, "{}", x => x.Headers.TryAddWithoutValidation("Retry-After", "30")));
        var provider = OpenAiCompatibleProvider.ForOpenAi(
            new HttpClient(handler),
            Key,
            "m",
            async (delay, cancellationToken) =>
            {
                delayEntered.TrySetResult(delay);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        using var cancellation = new CancellationTokenSource();

        var pending = provider.CompleteAsync(GoldenRequest(), cancellation.Token);
        (await delayEntered.Task.WaitAsync(TestContext.CurrentContext.CancellationToken)).Should().Be(TimeSpan.FromSeconds(30));
        await cancellation.CancelAsync();
        var act = () => pending.WaitAsync(TestContext.CurrentContext.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().ContainSingle();
    }

    [Test]
    public async Task TransportFailures_ThatNeverPass_AreRetriedThreeTimes_ThenTheFailureSurfaces()
    {
        var delays = new List<TimeSpan>();
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder(delays));

        var act = () => provider.CompleteAsync(GoldenRequest());

        var thrown = await act.Should().ThrowExactlyAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().BeNull();
        thrown.Which.Message.Should().Be("connection refused");
        handler.Requests.Should().HaveCount(4);
        delays.Should().HaveCount(3);
    }

    [Test]
    public async Task ATransportFailure_AfterTheCallerCancelled_IsNotRetried()
    {
        using var cancellation = new CancellationTokenSource();
        var delays = new List<TimeSpan>();
        var handler = new StubHandler(_ =>
        {
            cancellation.Cancel();
            throw new HttpRequestException("connection reset");
        });
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder(delays));

        var act = () => provider.CompleteAsync(GoldenRequest(), cancellation.Token);

        // HttpClient reports a failure after the caller cancelled as the cancellation.
        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().ContainSingle();
        delays.Should().BeEmpty();
    }

    [TestCase(HttpStatusCode.OK)]
    [TestCase(HttpStatusCode.InternalServerError)]
    [CancelAfter(10_000)]
    public async Task ABodyThatNeverEnds_FailsAtTheAttemptTimeout_AndIsNotRetried(HttpStatusCode status)
    {
        var delays = new List<TimeSpan>();
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StreamContent(new StalledStream()) });
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder(delays), attemptTimeout: TimeSpan.FromMilliseconds(100));

        var act = () => provider.CompleteAsync(GoldenRequest()).WaitAsync(TestContext.CurrentContext.CancellationToken);

        var thrown = await act.Should().ThrowExactlyAsync<TaskCanceledException>();
        thrown.Which.Message.Should().Be("OpenAI did not answer within 0.1 seconds.");
        thrown.Which.InnerException.Should().BeOfType<TimeoutException>();
        handler.Requests.Should().ContainSingle();
        delays.Should().BeEmpty();
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task ASendThatNeverAnswers_FailsAtTheAttemptTimeout()
    {
        var handler = new StallingHandler();
        var provider = OpenAiCompatibleProvider.ForAzure(
            new HttpClient(handler),
            "https://h.example/",
            Key,
            "dep",
            Recorder([]),
            attemptTimeout: TimeSpan.FromMilliseconds(100));

        var act = () => provider.CompleteAsync(GoldenRequest()).WaitAsync(TestContext.CurrentContext.CancellationToken);

        var thrown = await act.Should().ThrowExactlyAsync<TaskCanceledException>();
        thrown.Which.Message.Should().Be("Azure OpenAI did not answer within 0.1 seconds.");
        thrown.Which.InnerException.Should().BeOfType<TimeoutException>();
        handler.Calls.Should().Be(1);
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task CallerCancellation_WhileReadingTheBody_IsPlainCancellation()
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream(reading)) });
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder([]));
        using var cancellation = new CancellationTokenSource();

        var pending = provider.CompleteAsync(GoldenRequest(), cancellation.Token);
        await reading.Task.WaitAsync(TestContext.CurrentContext.CancellationToken);
        await cancellation.CancelAsync();
        var act = () => pending.WaitAsync(TestContext.CurrentContext.CancellationToken);

        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.Message.Should().NotContain("did not answer");
        (thrown.Which.InnerException is TimeoutException).Should().BeFalse();
    }

    [TestCase("Infinity")]
    [TestCase("1e300")]
    [TestCase("61000")]
    public async Task RetryAfterMs_LongerThanTheCap_IsNotRetried(string retryAfterMs)
    {
        var delays = new List<TimeSpan>();
        var handler = new StubHandler(_ => Json(HttpStatusCode.TooManyRequests, "{}", x => x.Headers.TryAddWithoutValidation("retry-after-ms", retryAfterMs)));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder(delays));

        var act = () => provider.CompleteAsync(GoldenRequest());

        var thrown = await act.Should().ThrowExactlyAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        thrown.Which.Message.Should().Be("OpenAI returned HTTP 429.");
        delays.Should().BeEmpty();
        handler.Requests.Should().ContainSingle();
    }

    [TestCase("-5")]
    [TestCase("abc")]
    [TestCase("NaN")]
    public async Task RetryAfterMs_ThatIsNotAWait_IsIgnored_InFavourOfRetryAfter(string retryAfterMs)
    {
        var delays = new List<TimeSpan>();
        var handler = new StubHandler(
            _ => Json(HttpStatusCode.TooManyRequests, "{}", x =>
            {
                x.Headers.TryAddWithoutValidation("retry-after-ms", retryAfterMs);
                x.Headers.TryAddWithoutValidation("Retry-After", "2");
            }),
            _ => Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder(delays));

        await provider.CompleteAsync(GoldenRequest());

        delays.Should().Equal(TimeSpan.FromSeconds(2));
    }

    [TestCase("-5")]
    [TestCase("abc")]
    public async Task RetryAfterMs_ThatIsNotAWait_WithoutRetryAfter_FallsBackToBackoff(string retryAfterMs)
    {
        var delays = new List<TimeSpan>();
        var handler = new StubHandler(
            _ => Json(HttpStatusCode.ServiceUnavailable, "{}", x => x.Headers.TryAddWithoutValidation("retry-after-ms", retryAfterMs)),
            _ => Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder(delays));

        await provider.CompleteAsync(GoldenRequest());

        delays.Should().ContainSingle().Which.Should().BeCloseTo(TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(161));
    }

    [TestCase("retry-after-ms", "60000", 60_000)]
    [TestCase("retry-after-ms", "0", 0)]
    [TestCase("Retry-After", "60", 60_000)]
    [TestCase("Retry-After", "0", 0)]
    [TestCase("Retry-After", "Wed, 21 Oct 2026 11:59:00 GMT", 0)]
    public async Task ARequestedWaitUpToTheCap_IsHonoured(string header, string value, int expectedMilliseconds)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        var delays = new List<TimeSpan>();
        var handler = new StubHandler(
            _ => Json(HttpStatusCode.TooManyRequests, "{}", x => x.Headers.TryAddWithoutValidation(header, value)),
            _ => Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder(delays), time);

        await provider.CompleteAsync(GoldenRequest());

        delays.Should().Equal(TimeSpan.FromMilliseconds(expectedMilliseconds));
        handler.Requests.Should().HaveCount(2);
    }

    [Test]
    public async Task ARedirect_IsNotFollowed_AndFailsWithItsStatus()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.TemporaryRedirect, "{}", x => x.Headers.Location = new Uri("https://elsewhere.example/v1/chat/completions")));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder([]));

        var act = () => provider.CompleteAsync(GoldenRequest());

        var thrown = await act.Should().ThrowExactlyAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
        thrown.Which.Message.Should().Be("OpenAI returned HTTP 307.");
        handler.Requests.Should().ContainSingle();
    }

    [TestCase(".")]
    [TestCase("..")]
    public void Azure_RefusesADotSegmentDeployment(string deployment)
    {
        var act = () => OpenAiCompatibleProvider.ForAzure(new HttpClient(), "https://h.example/", Key, deployment);

        act.Should().Throw<ArgumentException>().WithMessage("The Azure OpenAI deployment name cannot be a dot segment.*");
    }

    [TestCase("https://user:BODY-MARKER-7f3a@h.example/")]
    [TestCase("https://BODY-MARKER-7f3a@h.example/")]
    public void Azure_RefusesAnEndpointWithUserInformation_WithoutEchoingIt(string endpoint)
    {
        var act = () => OpenAiCompatibleProvider.ForAzure(new HttpClient(), endpoint, Key, "dep");

        var thrown = act.Should().Throw<ArgumentException>().WithMessage("The Azure OpenAI endpoint must not contain user information.*");
        thrown.Which.ToString().Should().NotContain(BodyMarker).And.NotContain("h.example");
    }

    [Test]
    public async Task Completion_WithoutAContentLength_LargerThanTheCap_Fails()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnseekableStream(new byte[OpenAiCompatibleProvider.MaxResponseBytes + 1]))
        };
        response.Content.Headers.ContentLength.Should().BeNull("the body is read until it passes the cap");
        var provider = OpenAi(_ => response);

        var act = () => provider.CompleteAsync(GoldenRequest());

        await act.Should().ThrowExactlyAsync<AiServiceException>().WithMessage("OpenAI returned a response larger than *");
    }

    [TestCase(false, false, "OpenAI returned HTTP 401 (invalid_api_key).")]
    [TestCase(true, false, "OpenAI returned HTTP 401.")]
    [TestCase(true, true, "OpenAI returned HTTP 401.")]
    public async Task AnErrorBody_IsReadOnlyUpTo64KB(bool overTheCap, bool chunked, string message)
    {
        var padding = new string(' ', overTheCap ? 64 * 1024 : 1024);
        var bytes = Encoding.UTF8.GetBytes($$"""{"error":{"code":"invalid_api_key"}{{padding}}}""");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = chunked ? new StreamContent(new UnseekableStream(bytes)) : new ByteArrayContent(bytes)
        });
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m", Recorder([]));

        var act = () => provider.CompleteAsync(GoldenRequest());

        (await act.Should().ThrowExactlyAsync<HttpRequestException>()).Which.Message.Should().Be(message);
    }

    [Test]
    public async Task Body_WithoutASystemPrompt_SendsOnlyTheConversation()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m");

        await provider.CompleteAsync(new LlmRequest { Messages = [new ChatMessage(ConversationRole.User, "Hi")], MaxTokens = 10, Temperature = 0m });

        var messages = Parse(handler.Requests.Single().Body).GetProperty("messages");
        messages.GetArrayLength().Should().Be(1);
        messages[0].GetProperty("role").GetString().Should().Be("user");
    }

    [Test]
    public async Task Body_CarriesTextThatNeedsEscaping_Unchanged()
    {
        const string text = "Čađa \"quoted\" \\ back\nnew line\t😀 </script>";
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), Key, "m");

        await provider.CompleteAsync(new LlmRequest { SystemPrompt = text, Messages = [new ChatMessage(ConversationRole.User, text)] });

        var messages = Parse(handler.Requests.Single().Body).GetProperty("messages");
        messages[0].GetProperty("content").GetString().Should().Be(text);
        messages[1].GetProperty("content").GetString().Should().Be(text);
    }

    [Test]
    public async Task HeadersAreSetPerRequest_NeverOnTheClient()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Completion), _ => Json(HttpStatusCode.OK, Completion));
        var client = new HttpClient(handler);
        var openAi = OpenAiCompatibleProvider.ForOpenAi(client, Key, "m");
        var azure = OpenAiCompatibleProvider.ForAzure(client, "https://h.example/", "azure-key", "dep");

        await openAi.CompleteAsync(GoldenRequest());
        await azure.CompleteAsync(GoldenRequest());

        client.DefaultRequestHeaders.Should().BeEmpty();
        handler.Requests[0].Headers.Should().NotContainKey("api-key");
        handler.Requests[1].Headers.Should().Contain("api-key", "azure-key").And.NotContainKey("Authorization");
    }

    [Test]
    public async Task CountTokens_EstimatesFourCharactersPerToken()
    {
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(), Key, "m");

        var count = await provider.CountTokensAsync(new string('x', 41));

        count.Tokens.Should().Be(10);
    }

#pragma warning disable CS0618 // the obsolete wrappers are what these tests pin
    [Test]
    public void ObsoleteWrappers_StillConstruct_AndReportTheirModel()
    {
        new OpenAiProvider(Key).Model.Should().Be("gpt-4o-mini");
        new OpenAiProvider(Key, "gpt-4o").Model.Should().Be("gpt-4o");
        new AzureOpenAiProvider("https://h.example/", Key, "dep").Model.Should().Be("dep");
        new ClaudeProvider(Key).Model.Should().Be("claude-sonnet-4-20250514");
    }

    [Test]
    public async Task ObsoleteWrappers_DelegateTheCompletion()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Completion));
        var client = new HttpClient(handler);

        var openAi = await new OpenAiProvider(client, Key, "gpt-4o").CompleteAsync(GoldenRequest());
        var azure = await new AzureOpenAiProvider(client, "https://h.example/", Key, "dep").CompleteAsync(GoldenRequest());

        openAi.Content.Should().Be("hello");
        openAi.Model.Should().Be("gpt-4o");
        azure.Content.Should().Be("hello");
        azure.Model.Should().Be("dep");
        handler.Requests.Select(x => x.Uri).Should().Equal(
            "https://api.openai.com/v1/chat/completions",
            "https://h.example/openai/deployments/dep/chat/completions?api-version=2024-10-21");
    }
#pragma warning restore CS0618

    [Test]
    public async Task Claude_SetsItsHeadersPerRequest_OnTheClientItIsGiven()
    {
        var handler = new StubHandler(_ => Json(
            HttpStatusCode.OK,
            """{"model":"claude-x","content":[{"type":"text","text":"hi"}],"usage":{"input_tokens":3,"output_tokens":4},"stop_reason":"end_turn"}"""));
        var client = new HttpClient(handler);
        var provider = new ClaudeProvider(client, Key, "claude-x");

        var response = await provider.CompleteAsync(GoldenRequest());

        response.Content.Should().Be("hi");
        client.DefaultRequestHeaders.Should().BeEmpty();
        var sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Uri.Should().Be("https://api.anthropic.com/v1/messages");
        sent.Headers.Should().Contain("x-api-key", Key).And.Contain("anthropic-version", "2023-06-01");
    }

    internal static LlmRequest GoldenRequest()
    {
        return new LlmRequest
        {
            SystemPrompt = "You are terse.",
            Messages =
            [
                new ChatMessage(ConversationRole.User, "Hi"),
                new ChatMessage(ConversationRole.Assistant, "Hello"),
                new ChatMessage(ConversationRole.User, "Count tables")
            ],
            MaxTokens = 256,
            Temperature = 0.2m
        };
    }

    internal static HttpResponseMessage Json(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        configure?.Invoke(response);

        return response;
    }

    private static OpenAiCompatibleProvider OpenAi(Func<HttpRequestMessage, HttpResponseMessage> respond, string model = "m")
    {
        return OpenAiCompatibleProvider.ForOpenAi(new HttpClient(new StubHandler(respond)), Key, model, Recorder([]));
    }

    private static Func<TimeSpan, CancellationToken, Task> Recorder(List<TimeSpan> delays)
    {
        return (delay, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            delays.Add(delay);

            return Task.CompletedTask;
        };
    }

    private static JsonElement Golden(string name)
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Unit", "LlmProviders", "WireGolden", name);

        return Parse(File.ReadAllText(path));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }

    internal sealed record SentRequest(string Method, string Uri, Dictionary<string, string> Headers, string? ContentType, string Body);

    /// <summary>Answers each request with the next responder (the last one repeats) and records what was sent.</summary>
    internal sealed class StubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(x => x.Key, x => string.Join(",", x.Value));
            Requests.Add(new SentRequest(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                headers,
                request.Content?.Headers.ContentType?.MediaType,
                body));

            return responders[Math.Min(Requests.Count, responders.Length) - 1](request);
        }
    }

    /// <summary>Never answers; ends only when the request is cancelled.</summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            throw new InvalidOperationException("Unreachable.");
        }
    }

    /// <summary>A response body that never delivers a byte; a read ends only when it is cancelled.</summary>
    private sealed class StalledStream(TaskCompletionSource? reading = null) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reading?.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body whose length is unknown up front, as with chunked transfer encoding.</summary>
    private sealed class UnseekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
