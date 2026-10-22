using System.Net;
using System.Text.Json;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data.Enums;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// OpenAI and Azure OpenAI cache prompts on their own: a message marked as a cache breakpoint goes as the plain string it
/// always was, and consecutive user messages stay separate messages, in order.
/// </summary>
[TestFixture]
public class CacheBreakpointWireShapeTests
{
    private const string Completion = """{"id":"x","object":"chat.completion","model":"gpt-4o-mini","choices":[{"index":0,"message":{"role":"assistant","content":"SELECT 1"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}""";
    private const string ExpectedMessages = """
        [
          { "role": "system", "content": "You are a SQL expert." },
          { "role": "user", "content": "DATABASE CONTEXT:\nTABLE sales.orders (id, status)" },
          { "role": "user", "content": "USER QUESTION: orders placed last week" }
        ]
        """;

    [Test]
    public async Task OpenAi_SendsACacheBreakpointMessage_AsAPlainString()
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForOpenAi(new HttpClient(handler), TestCredentials.ProviderSecretKey, "gpt-4o-mini");

        await provider.CompleteAsync(Request());

        AssertPlainMessages(handler);
    }

    [Test]
    public async Task Azure_SendsACacheBreakpointMessage_AsAPlainString()
    {
        var handler = new OpenAiCompatibleProviderTests.StubHandler(_ => OpenAiCompatibleProviderTests.Json(HttpStatusCode.OK, Completion));
        var provider = OpenAiCompatibleProvider.ForAzure(new HttpClient(handler), "https://h.example/", TestCredentials.ProviderSecretKey, "dep");

        await provider.CompleteAsync(Request());

        AssertPlainMessages(handler);
    }

    private static void AssertPlainMessages(OpenAiCompatibleProviderTests.StubHandler handler)
    {
        var body = handler.Requests.Should().ContainSingle().Subject.Body;
        body.Should().NotContain("cache_control").And.NotContain("CacheBreakpoint");

        using var sent = JsonDocument.Parse(body);
        using var expected = JsonDocument.Parse(ExpectedMessages);
        JsonElement.DeepEquals(sent.RootElement.GetProperty("messages"), expected.RootElement).Should().BeTrue("the body was {0}", body);
    }

    private static LlmRequest Request()
    {
        return new LlmRequest
        {
            SystemPrompt = "You are a SQL expert.",
            Messages =
            [
                new ChatMessage(ConversationRole.User, "DATABASE CONTEXT:\nTABLE sales.orders (id, status)") { CacheBreakpoint = true },
                new ChatMessage(ConversationRole.User, "USER QUESTION: orders placed last week")
            ]
        };
    }
}
