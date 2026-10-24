using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class McpSessionHeaderTests
{
    [Test]
    public async Task Initialize_WithAnUnknownSessionId_DoesNotOpenASession()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddMcpServer()
            .WithHttpTransport(x => x.SessionMode = HttpServerSessionMode.StatefulForInitializeClients);
        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        await using var _ = app;
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("Accept", "application/json, text/event-stream");
        request.Headers.Add("Mcp-Session-Id", "does-not-exist");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.Contains("Mcp-Session-Id").Should().BeFalse();
    }
}
