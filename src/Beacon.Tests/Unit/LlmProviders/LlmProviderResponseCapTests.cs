using System.Net;
using System.Text;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Exceptions;
using Beacon.Tests.Common;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// The Claude and Bedrock providers read a successful response only up to their cap, and a failed call names Beacon's
/// wording and the failure's type and code, never the service's or the response's text.
/// </summary>
[TestFixture]
[CancelAfter(10_000)]
public class LlmProviderResponseCapTests
{
    private const string ServiceText = "SERVICE-TEXT-91fe";

    [Test]
    public async Task Claude_AResponseLargerThanTheCap_Fails()
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[ClaudeProvider.MaxResponseBytes + 1])
        });
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-x");

        var act = () => provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        await act.Should().ThrowExactlyAsync<AiServiceException>().WithMessage("Claude API returned a response larger than *");
    }

    [Test]
    public async Task Claude_AResponseWithoutALength_LargerThanTheCap_Fails()
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NoLengthStream(new byte[ClaudeProvider.MaxResponseBytes + 1]))
        });
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-x");

        var act = () => provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        await act.Should().ThrowExactlyAsync<AiServiceException>().WithMessage("Claude API returned a response larger than *");
    }

    [Test]
    public async Task Claude_AResponseWithinTheCap_IsRead()
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(
            HttpStatusCode.OK,
            """{"model":"claude-x","content":[{"type":"text","text":"SELECT 1"}],"usage":{"input_tokens":3,"output_tokens":2},"stop_reason":"end_turn"}"""));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-x");

        var response = await provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        response.Content.Should().Be("SELECT 1");
        response.InputTokens.Should().Be(3);
    }

    [Test]
    public async Task Claude_AnUnreadableResponse_FailsWithoutItsText()
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(HttpStatusCode.OK, $"{{\"model\": {ServiceText}"));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-x");

        var act = () => provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        var thrown = await act.Should().ThrowExactlyAsync<AiServiceException>().WithMessage("Claude API request failed (JsonException*");
        thrown.Which.Message.Should().NotContain(ServiceText);
    }

    [TestCase("anthropic.claude-3-haiku")]
    [TestCase("meta.llama3-1-8b")]
    public async Task Bedrock_AResponseLargerThanTheCap_Fails(string modelId)
    {
        var provider = Bedrock(modelId, new InvokeModelResponse { Body = new MemoryStream(new byte[BedrockProvider.MaxResponseBytes + 1]) });

        var act = () => provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        await act.Should().ThrowExactlyAsync<AiServiceException>().WithMessage("AWS Bedrock returned a response larger than *");
    }

    [Test]
    public async Task Bedrock_AResponseWithinTheCap_IsRead()
    {
        var body = """{"content":[{"type":"text","text":"SELECT 1"}],"usage":{"input_tokens":3,"output_tokens":2},"stop_reason":"end_turn"}""";
        var provider = Bedrock("anthropic.claude-3-haiku", new InvokeModelResponse { Body = new MemoryStream(Encoding.UTF8.GetBytes(body)) });

        var response = await provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        response.Content.Should().Be("SELECT 1");
        response.OutputTokens.Should().Be(2);
    }

    [TestCase("anthropic.claude-3-haiku")]
    [TestCase("meta.llama3-1-8b")]
    public async Task Bedrock_ARequestFailure_NamesTheTypeAndCode_NeverTheServicesText(string modelId)
    {
        var failures = new Exception[]
        {
            new ValidationException(ServiceText),
            new AccessDeniedException(ServiceText),
            new AmazonBedrockRuntimeException(ServiceText, ErrorType.Sender, "BadThing", "r", HttpStatusCode.BadRequest),
            new InvalidOperationException(ServiceText)
        };

        foreach (var failure in failures)
        {
            var client = new Mock<IAmazonBedrockRuntime>();
            client
                .Setup(x => x.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(failure);
            var provider = new BedrockProvider(client.Object, modelId);

            var act = () => provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

            var thrown = await act.Should().ThrowExactlyAsync<AiServiceException>();
            thrown.Which.Message.Should().StartWith("AWS Bedrock").And.Contain(failure.GetType().Name).And.NotContain(ServiceText);
            thrown.Which.InnerException.Should().BeSameAs(failure, "the failure stays on the exception for the log's type and code");
        }
    }

    private static BedrockProvider Bedrock(string modelId, InvokeModelResponse response)
    {
        var client = new Mock<IAmazonBedrockRuntime>();
        client
            .Setup(x => x.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        return new BedrockProvider(client.Object, modelId);
    }

    private sealed class NoLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
