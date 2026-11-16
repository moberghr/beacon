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
/// Claude Opus 4.7 and later, Sonnet and Haiku 5 and later, Fable and Mythos refuse a request that sets a sampling
/// parameter (HTTP 400). Both Claude providers, the Anthropic API and Bedrock, send the request's temperature only to a
/// model known to take it (Claude 3.x, and Opus, Sonnet and Haiku up to 4.6) and leave it out for every other model id.
/// </summary>
[TestFixture]
public class ClaudeSamplingTests
{
    private const string Completion = """{"model":"claude","content":[{"type":"text","text":"SELECT 1"}],"stop_reason":"end_turn","usage":{"input_tokens":12,"output_tokens":5}}""";

    [TestCase("claude-3-haiku-20240307")]
    [TestCase("claude-3-7-sonnet-latest")]
    [TestCase("claude-sonnet-4-20250514")]
    [TestCase("claude-opus-4-1-20250805")]
    [TestCase("claude-opus-4-6")]
    [TestCase("CLAUDE-SONNET-4-6")]
    [TestCase("anthropic.claude-3-haiku-20240307-v1:0")]
    [TestCase("eu.anthropic.claude-sonnet-4-5-20250929-v1:0")]
    [TestCase("global.anthropic.claude-opus-4-6-v1")]
    [TestCase("arn:aws:bedrock:eu-central-1:123456789012:inference-profile/eu.anthropic.claude-haiku-4-5-20251001-v1:0")]
    public void AcceptsTemperature_ForClaude3AndOpusSonnetHaikuUpTo46_IsTrue(string modelId)
    {
        ClaudeSampling.AcceptsTemperature(modelId).Should().BeTrue();
    }

    [TestCase("claude-opus-4-7")]
    [TestCase("claude-opus-4-8")]
    [TestCase("claude-opus-4-7:1m")]
    [TestCase("claude-opus-5")]
    [TestCase("claude-opus-5-5")]
    [TestCase("claude-sonnet-5")]
    [TestCase("claude-sonnet-5-5")]
    [TestCase("claude-haiku-5-5")]
    [TestCase("claude-fable-5-2")]
    [TestCase("claude-mythos-5-2")]
    [TestCase("eu.anthropic.claude-sonnet-5-5")]
    [TestCase("anthropic.claude-opus-4-7-v1:0")]
    [TestCase("arn:aws:bedrock:eu-central-1:123456789012:inference-profile/global.anthropic.claude-sonnet-5-5")]
    public void AcceptsTemperature_ForModelsThatRefuseSampling_IsFalse(string modelId)
    {
        ClaudeSampling.AcceptsTemperature(modelId).Should().BeFalse();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("claude-x")]
    [TestCase("claude-sonnet")]
    [TestCase("claude-instant-v1")]
    [TestCase("arn:aws:bedrock:eu-central-1:123456789012:application-inference-profile/a1b2c3")]
    public void AcceptsTemperature_ForAnUnrecognisedModelId_IsFalse(string? modelId)
    {
        ClaudeSampling.AcceptsTemperature(modelId).Should().BeFalse();
    }

    [TestCase("claude-opus-4-7")]
    [TestCase("claude-opus-5-5")]
    [TestCase("claude-sonnet-5-5")]
    [TestCase("claude-haiku-5-5")]
    [TestCase("claude-fable-5-2")]
    public async Task ClaudeProvider_ForAModelThatRefusesSampling_SendsNoTemperature(string model)
    {
        var body = await ClaudeRequestBody(model);

        body.TryGetProperty("temperature", out _).Should().BeFalse();
        body.GetProperty("max_tokens").GetInt32().Should().Be(256);
    }

    [TestCase("claude-sonnet-4-6")]
    [TestCase("claude-haiku-4-5-20251001")]
    [TestCase("claude-3-5-sonnet-20241022")]
    public async Task ClaudeProvider_ForAModelThatTakesSampling_SendsTheRequestTemperature(string model)
    {
        var body = await ClaudeRequestBody(model);

        body.GetProperty("temperature").GetDecimal().Should().Be(0.2m);
    }

    [TestCase("eu.anthropic.claude-sonnet-5-5")]
    [TestCase("global.anthropic.claude-haiku-5-5")]
    [TestCase("anthropic.claude-opus-4-7")]
    public async Task BedrockProvider_ForAModelThatRefusesSampling_SendsNoTemperature(string model)
    {
        var body = await BedrockRequestBody(model);

        body.TryGetProperty("temperature", out _).Should().BeFalse();
        body.GetProperty("anthropic_version").GetString().Should().Be("bedrock-2023-05-31");
        body.GetProperty("max_tokens").GetInt32().Should().Be(256);
    }

    [TestCase("eu.anthropic.claude-sonnet-4-5-20250929-v1:0")]
    [TestCase("global.anthropic.claude-opus-4-6-v1")]
    [TestCase("anthropic.claude-3-5-haiku-20241022-v1:0")]
    public async Task BedrockProvider_ForAModelThatTakesSampling_SendsTheRequestTemperature(string model)
    {
        var body = await BedrockRequestBody(model);

        body.GetProperty("temperature").GetDecimal().Should().Be(0.2m);
    }

    private static async Task<JsonElement> ClaudeRequestBody(string model)
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(HttpStatusCode.OK, Completion));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, model);

        await provider.CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        return Parse(handler.Requests.Single().Body);
    }

    private static async Task<JsonElement> BedrockRequestBody(string model)
    {
        string? sent = null;
        var client = new Mock<IAmazonBedrockRuntime>();
        client
            .Setup(x => x.InvokeModelAsync(It.IsAny<InvokeModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<InvokeModelRequest, CancellationToken>((x, _) => sent = new StreamReader(x.Body).ReadToEnd())
            .ReturnsAsync(() => new InvokeModelResponse { Body = new MemoryStream(Encoding.UTF8.GetBytes(Completion)) });

        await new BedrockProvider(client.Object, model).CompleteAsync(OpenAiCompatibleProviderTests.GoldenRequest());

        return Parse(sent!);
    }

    private static JsonElement Parse(string body)
    {
        using var document = JsonDocument.Parse(body);

        return document.RootElement.Clone();
    }
}
