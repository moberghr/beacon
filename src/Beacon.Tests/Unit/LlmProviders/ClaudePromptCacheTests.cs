using System.Net;
using System.Text.Json;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data.Enums;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// The Claude provider asks for prompt caching only on a message marked as a cache breakpoint: that message goes as one
/// text block carrying an ephemeral cache point, every other message and the system prompt as the plain strings they always
/// were. The usage's cache read and cache write counts are read back into the response.
/// </summary>
[TestFixture]
public class ClaudePromptCacheTests
{
    private const string SystemPrompt = "You are a SQL expert.";
    private const string ContextMessage = "DATABASE CONTEXT:\nTABLE sales.orders (id, status)";
    private const string QuestionMessage = "USER QUESTION: orders placed last week";
    private const string Completion = """{"model":"claude-test","content":[{"type":"text","text":"SELECT 1"}],"stop_reason":"end_turn","usage":{"input_tokens":12,"output_tokens":5,"cache_read_input_tokens":3000,"cache_creation_input_tokens":1500}}""";
    private const string CompletionWithoutCache = """{"model":"claude-test","content":[{"type":"text","text":"SELECT 1"}],"stop_reason":"end_turn","usage":{"input_tokens":12,"output_tokens":5}}""";

    [Test]
    public async Task CompleteAsync_WithACacheBreakpoint_SendsThatMessageAsACachedBlock_AndTheRestAsStrings()
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(HttpStatusCode.OK, Completion));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-test");

        await provider.CompleteAsync(Request(cacheBreakpoint: true));

        var body = Body(handler);
        body.GetProperty("system").GetString().Should().Be(SystemPrompt);
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        messages.Should().HaveCount(2);
        messages.Should().OnlyContain(x => x.GetProperty("role").GetString() == "user");
        var block = messages[0].GetProperty("content").EnumerateArray().Should().ContainSingle().Subject;
        block.GetProperty("type").GetString().Should().Be("text");
        block.GetProperty("text").GetString().Should().Be(ContextMessage);
        block.GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");
        messages[1].GetProperty("content").GetString().Should().Be(QuestionMessage);
    }

    [Test]
    public async Task CompleteAsync_WithoutACacheBreakpoint_SendsPlainStrings()
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(HttpStatusCode.OK, Completion));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-test");

        await provider.CompleteAsync(Request(cacheBreakpoint: false));

        var body = Body(handler);
        body.GetProperty("system").GetString().Should().Be(SystemPrompt);
        body.GetProperty("messages").EnumerateArray().Select(x => x.GetProperty("content").GetString())
            .Should().Equal(ContextMessage, QuestionMessage);
        handler.Requests.Single().Body.Should().NotContain("cache_control");
    }

    [Test]
    public async Task CompleteAsync_ReadsTheCacheReadAndCacheWriteTokens()
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(HttpStatusCode.OK, Completion));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-test");

        var response = await provider.CompleteAsync(Request(cacheBreakpoint: true));

        response.InputTokens.Should().Be(12);
        response.OutputTokens.Should().Be(5);
        response.CacheReadInputTokens.Should().Be(3000);
        response.CacheWriteInputTokens.Should().Be(1500);
        response.PromptCacheHit.Should().BeTrue();
    }

    [Test]
    public async Task CompleteAsync_WithoutCacheUsage_ReportsNoCacheTokens()
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(HttpStatusCode.OK, CompletionWithoutCache));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-test");

        var response = await provider.CompleteAsync(Request(cacheBreakpoint: false));

        response.CacheReadInputTokens.Should().Be(0);
        response.CacheWriteInputTokens.Should().Be(0);
        response.PromptCacheHit.Should().BeFalse();
    }

    // Anthropic's prices per million tokens: input 3.00, output 15.00, a cache read 0.30, a cache write 3.75.
    [Test]
    public async Task CompleteAsync_CostsEachTokenTypeAtItsRate()
    {
        const string completion = """{"model":"claude-test","content":[{"type":"text","text":"SELECT 1"}],"stop_reason":"end_turn","usage":{"input_tokens":2000000,"output_tokens":100000,"cache_read_input_tokens":1000000,"cache_creation_input_tokens":400000}}""";
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(HttpStatusCode.OK, completion));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-test");

        var response = await provider.CompleteAsync(Request(cacheBreakpoint: true));

        // 6.00 input + 1.50 output + 0.30 cache read + 1.50 cache write.
        response.Cost.Should().Be(9.30m);
    }

    [Test]
    public async Task CompleteAsync_WithNullCacheUsage_ReportsNoCacheTokens()
    {
        const string completion = """{"model":"claude-test","content":[{"type":"text","text":"SELECT 1"}],"stop_reason":"end_turn","usage":{"input_tokens":1000000,"output_tokens":0,"cache_read_input_tokens":null,"cache_creation_input_tokens":null}}""";
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(HttpStatusCode.OK, completion));
        var provider = new ClaudeProvider(new HttpClient(handler), TestCredentials.ProviderSecretKey, "claude-test");

        var response = await provider.CompleteAsync(Request(cacheBreakpoint: false));

        response.CacheReadInputTokens.Should().Be(0);
        response.CacheWriteInputTokens.Should().Be(0);
        response.PromptCacheHit.Should().BeFalse();
        response.Cost.Should().Be(3.00m);
    }

    private static LlmRequest Request(bool cacheBreakpoint)
    {
        return new LlmRequest
        {
            SystemPrompt = SystemPrompt,
            Messages =
            [
                new ChatMessage(ConversationRole.User, ContextMessage) { CacheBreakpoint = cacheBreakpoint },
                new ChatMessage(ConversationRole.User, QuestionMessage)
            ]
        };
    }

    private static JsonElement Body(OpenAiCompatibleProviderTests.StubHandler handler)
    {
        using var document = JsonDocument.Parse(handler.Requests.Single().Body);

        return document.RootElement.Clone();
    }
}
