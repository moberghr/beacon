using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Beacon.AI.Services.LlmProviders;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// A Bedrock model that is neither Claude nor Llama (Amazon Nova, DeepSeek, Mistral) goes through the Converse API: the
/// system prompt, the messages in order and the limits are sent, the text blocks come back as the answer, and Claude and
/// Llama keep their own request formats.
/// </summary>
[TestFixture]
public class BedrockConverseTests
{
    [TestCase("eu.amazon.nova-2-lite-v1:0")]
    [TestCase("deepseek.v3.2")]
    [TestCase("mistral.mistral-large-3-675b-instruct")]
    public async Task Converse_SendsTheRequest_AndReturnsTheText(string model)
    {
        ConverseRequest? sent = null;
        var client = Client(x => sent = x, Response("SELECT 1", StopReason.End_turn));

        var response = await new BedrockProvider(client.Object, model).CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        sent!.ModelId.Should().Be(model);
        sent.System.Should().ContainSingle().Which.Text.Should().Be("You are terse.");
        sent.Messages.Select(x => x.Role.Value).Should().Equal("user", "assistant", "user");
        sent.Messages.Select(x => x.Content.Single().Text).Should().Equal("Hi", "Hello", "Count tables");
        sent.InferenceConfig.MaxTokens.Should().Be(256);
        sent.InferenceConfig.Temperature.Should().BeApproximately(0.2f, 0.0001f);
        response.Content.Should().Be("SELECT 1");
        response.InputTokens.Should().Be(1_000_000);
        response.OutputTokens.Should().Be(1_000_000);
        response.Model.Should().Be(model);
        response.Truncated.Should().BeFalse();
        client.Verify(x => x.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Converse_LeavesTheSystemOut_WithoutASystemPrompt()
    {
        ConverseRequest? sent = null;
        var client = Client(x => sent = x, Response("pong", StopReason.End_turn));
        var request = new LlmRequest
        {
            Messages = [new ChatMessage(Beacon.Core.Data.Enums.ConversationRole.User, "ping")],
            MaxTokens = 16,
            Temperature = 0m
        };

        await new BedrockProvider(client.Object, "deepseek.v3.2").CompleteAsync(request);

        (sent!.System ?? []).Should().BeEmpty();
    }

    [Test]
    public async Task Converse_SkipsAReasoningBlock_AndReportsTruncation()
    {
        var response = Response("SELECT 2", StopReason.Max_tokens);
        response.Output.Message.Content.Insert(0, new ContentBlock { ReasoningContent = new ReasoningContentBlock() });
        var client = Client(_ => { }, response);

        var result = await new BedrockProvider(client.Object, "deepseek.v3.2").CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        result.Content.Should().Be("SELECT 2");
        result.Truncated.Should().BeTrue();
    }

    [TestCase("mistral.mistral-large-3-675b-instruct", 2.00)]
    [TestCase("deepseek.v3.2", 2.47)]
    public async Task Converse_CostsAResponse_AtTheModelsPrices(string model, double expected)
    {
        var client = Client(_ => { }, Response("ok", StopReason.End_turn));

        var response = await new BedrockProvider(client.Object, model).CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        response.Cost.Should().Be((decimal)expected);
    }

    private static Mock<IAmazonBedrockRuntime> Client(Action<ConverseRequest> capture, ConverseResponse response)
    {
        var client = new Mock<IAmazonBedrockRuntime>();
        client
            .Setup(x => x.ConverseAsync(It.IsAny<ConverseRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ConverseRequest, CancellationToken>((x, _) => capture(x))
            .ReturnsAsync(response);

        return client;
    }

    private static ConverseResponse Response(string text, StopReason stopReason)
    {
        return new ConverseResponse
        {
            Output = new ConverseOutput
            {
                Message = new Message
                {
                    Role = ConversationRole.Assistant,
                    Content = [new ContentBlock { Text = text }]
                }
            },
            StopReason = stopReason,
            Usage = new TokenUsage { InputTokens = 1_000_000, OutputTokens = 1_000_000, TotalTokens = 2_000_000 }
        };
    }
}
