using System.Net;
using System.Text;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Beacon.AI.Services.LlmProviders;
using Beacon.Tests.Common;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// A Claude model that thinks first answers with a thinking block before its text. Both Claude providers return the text
/// blocks, not the first block, and cost the response at the model's own prices.
/// </summary>
[TestFixture]
public class ClaudeResponseTextTests
{
    private const string ThinkingFirst = """{"model":"claude-haiku-5-5","content":[{"type":"thinking","thinking":"count the rows","signature":"s"},{"type":"text","text":"SELECT COUNT(*) FROM customer"}],"stop_reason":"end_turn","usage":{"input_tokens":12,"output_tokens":5}}""";
    private const string TwoTexts = """{"model":"claude","content":[{"type":"text","text":"SELECT 1"},{"type":"redacted_thinking","data":"x"},{"type":"text","text":" -- done"}],"stop_reason":"end_turn","usage":{"input_tokens":12,"output_tokens":5}}""";
    private const string ThinkingOnly = """{"model":"claude","content":[{"type":"thinking","thinking":"hm","signature":"s"}],"stop_reason":"max_tokens","usage":{"input_tokens":12,"output_tokens":16}}""";
    private const string Million = """{"model":"claude","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1000000,"output_tokens":1000000}}""";

    [TestCase(ThinkingFirst, "SELECT COUNT(*) FROM customer")]
    [TestCase(TwoTexts, "SELECT 1 -- done")]
    [TestCase(ThinkingOnly, "")]
    public async Task Claude_ReturnsTheTextBlocks_NotTheFirstBlock(string completion, string expected)
    {
        var response = await ClaudeResponse(completion, "claude-haiku-5-5");

        response.Content.Should().Be(expected);
    }

    [TestCase(ThinkingFirst, "SELECT COUNT(*) FROM customer")]
    [TestCase(TwoTexts, "SELECT 1 -- done")]
    [TestCase(ThinkingOnly, "")]
    public async Task Bedrock_ReturnsTheTextBlocks_NotTheFirstBlock(string completion, string expected)
    {
        var response = await BedrockResponse(completion, "eu.anthropic.claude-haiku-5-5");

        response.Content.Should().Be(expected);
    }

    [TestCase("claude-opus-5-5", 24.00)]
    [TestCase("claude-sonnet-5-5", 12.00)]
    [TestCase("claude-haiku-5-5", 0.60)]
    [TestCase("claude-fable-5-2", 48.00)]
    [TestCase("claude-sonnet-4-6", 18.00)]
    public async Task Claude_CostsAResponse_AtTheModelsPrices(string model, double expected)
    {
        var response = await ClaudeResponse(Million, model);

        response.Cost.Should().Be((decimal)expected);
    }

    [TestCase("eu.anthropic.claude-opus-5-5", 24.00)]
    [TestCase("eu.anthropic.claude-sonnet-5-5", 12.00)]
    [TestCase("eu.anthropic.claude-haiku-5-5", 0.60)]
    [TestCase("global.anthropic.claude-fable-5-2", 48.00)]
    [TestCase("eu.anthropic.claude-sonnet-4-5-20250929-v1:0", 18.00)]
    public async Task Bedrock_CostsAClaudeResponse_AtTheModelsPrices(string model, double expected)
    {
        var response = await BedrockResponse(Million, model);

        response.Cost.Should().Be((decimal)expected);
    }

    private static async Task<Beacon.Core.Models.Ai.LlmResponse> ClaudeResponse(string completion, string model)
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(HttpStatusCode.OK, completion));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, model);

        return await provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());
    }

    private static async Task<Beacon.Core.Models.Ai.LlmResponse> BedrockResponse(string completion, string model)
    {
        var client = new Mock<IAmazonBedrockRuntime>();
        client
            .Setup(x => x.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new InvokeModelResponse { Body = new MemoryStream(Encoding.UTF8.GetBytes(completion)) });

        return await new BedrockProvider(client.Object, model).CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());
    }
}
