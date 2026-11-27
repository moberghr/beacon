using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Beacon.AI.Services.LlmProviders;
using Beacon.Tests.Common;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// The Anthropic API refuses a null <c>system</c> (HTTP 400). Both Claude providers, the Anthropic API and Bedrock, send
/// the field only when the request has a system prompt, so a request without one (the connection test) goes through.
/// </summary>
[TestFixture]
public class ClaudeSystemPromptTests
{
    private const string Completion = """{"model":"claude","content":[{"type":"text","text":"pong"}],"stop_reason":"end_turn","usage":{"input_tokens":12,"output_tokens":5}}""";

    [TestCase(null)]
    [TestCase("")]
    public async Task Claude_LeavesTheSystemFieldOut_WithoutASystemPrompt(string? systemPrompt)
    {
        var body = await ClaudeRequestBody(systemPrompt);

        body.TryGetProperty("system", out _).Should().BeFalse();
        body.GetProperty("messages").GetArrayLength().Should().Be(1);
    }

    [Test]
    public async Task Claude_SendsTheSystemPrompt_WhenTheRequestHasOne()
    {
        var body = await ClaudeRequestBody("You are terse.");

        body.GetProperty("system").GetString().Should().Be("You are terse.");
    }

    [TestCase(null)]
    [TestCase("")]
    public async Task Bedrock_LeavesTheSystemFieldOut_WithoutASystemPrompt(string? systemPrompt)
    {
        var body = await BedrockRequestBody(systemPrompt);

        body.TryGetProperty("system", out _).Should().BeFalse();
        body.GetProperty("messages").GetArrayLength().Should().Be(1);
    }

    [Test]
    public async Task Bedrock_SendsTheSystemPrompt_WhenTheRequestHasOne()
    {
        var body = await BedrockRequestBody("You are terse.");

        body.GetProperty("system").GetString().Should().Be("You are terse.");
    }

    private static LlmRequest Request(string? systemPrompt)
    {
        return new LlmRequest
        {
            SystemPrompt = systemPrompt,
            Messages = [new ChatMessage(Beacon.Core.Data.Enums.ConversationRole.User, "ping")],
            MaxTokens = 16,
            Temperature = 0m
        };
    }

    private static async Task<JsonElement> ClaudeRequestBody(string? systemPrompt)
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(HttpStatusCode.OK, Completion));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-haiku-5-5");

        await provider.CompleteAsync(Request(systemPrompt));

        return Parse(handler.Requests.Single().Body);
    }

    private static async Task<JsonElement> BedrockRequestBody(string? systemPrompt)
    {
        string? sent = null;
        var client = new Mock<IAmazonBedrockRuntime>();
        client
            .Setup(x => x.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<InvokeModelRequest, CancellationToken>((x, _) => sent = new StreamReader(x.Body).ReadToEnd())
            .ReturnsAsync(() => new InvokeModelResponse { Body = new MemoryStream(Encoding.UTF8.GetBytes(Completion)) });

        await new BedrockProvider(client.Object, "eu.anthropic.claude-haiku-5-5").CompleteAsync(Request(systemPrompt));

        return Parse(sent!);
    }

    private static JsonElement Parse(string body)
    {
        using var document = JsonDocument.Parse(body);

        return document.RootElement.Clone();
    }
}
