using Beacon.AI.Services.LlmProviders;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// A Claude response on Bedrock is read into token counts — the prompt cache's read and write counts included, absent or null
/// read as zero — and costed at the model's prices, a cache read at a tenth of the input price and a cache write at a
/// quarter more.
/// </summary>
[TestFixture]
public class BedrockClaudeResponseTests
{
    // Claude 3.5 Haiku: 0.80 input and 4.00 output per million tokens.
    private const string HaikuModel = "anthropic.claude-3-5-haiku-20241022-v1:0";

    [Test]
    public void ParseClaudeResponse_ReadsCacheTokens_AndCostsEachTokenTypeAtItsRate()
    {
        const string body = """{"id":"x","type":"message","role":"assistant","model":"claude","content":[{"type":"text","text":"SELECT 1"}],"stop_reason":"end_turn","usage":{"input_tokens":2000000,"output_tokens":100000,"cache_read_input_tokens":1000000,"cache_creation_input_tokens":400000}}""";

        var response = Provider().ParseClaudeResponse(body);

        response.Content.Should().Be("SELECT 1");
        response.Model.Should().Be(HaikuModel);
        response.InputTokens.Should().Be(2_000_000);
        response.OutputTokens.Should().Be(100_000);
        response.CacheReadInputTokens.Should().Be(1_000_000);
        response.CacheWriteInputTokens.Should().Be(400_000);
        response.PromptCacheHit.Should().BeTrue();
        response.Truncated.Should().BeFalse();

        // 1.60 input + 0.40 output + 0.08 cache read + 0.40 cache write.
        response.Cost.Should().Be(2.48m);
    }

    [TestCase("""{"input_tokens":2000000,"output_tokens":100000}""")]
    [TestCase("""{"input_tokens":2000000,"output_tokens":100000,"cache_read_input_tokens":null,"cache_creation_input_tokens":null}""")]
    public void ParseClaudeResponse_WithoutCacheCounts_ReportsNoCacheTokens(string usage)
    {
        var body = $$"""{"content":[{"type":"text","text":"SELECT 1"}],"stop_reason":"max_tokens","usage":{{usage}}}""";

        var response = Provider().ParseClaudeResponse(body);

        response.CacheReadInputTokens.Should().Be(0);
        response.CacheWriteInputTokens.Should().Be(0);
        response.PromptCacheHit.Should().BeFalse();
        response.Truncated.Should().BeTrue();
        response.Cost.Should().Be(2.00m);
    }

    private static BedrockProvider Provider()
    {
        return new BedrockProvider(TestCredentials.AwsAccessKeyId, TestCredentials.AwsSecretAccessKey, "eu-central-1", HaikuModel);
    }
}
