using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Beacon.AI.Services.Documentation;
using Beacon.Api.Endpoints;
using Beacon.Core.Handlers.Projects;

namespace Beacon.Tests.Unit;

[TestFixture]
public class DocumentationContentSafetyTests
{
    [TestCase("<script>alert(1)</script>")]
    [TestCase("<img src=x onerror=alert(1)>")]
    [TestCase("<iframe src=\"https://example.com\"></iframe>")]
    public void ConvertToHtml_EscapesRawHtml(string markup)
    {
        var html = ProjectDocumentationService.ConvertToHtml($"# Title\n\n{markup}\n");

        var body = html[html.IndexOf("<body>", StringComparison.Ordinal)..];
        body.Should().NotContain("<script");
        body.Should().NotContain("<img");
        body.Should().NotContain("<iframe");
        body.Should().Contain("&lt;");
    }

    [Test]
    public void ConvertToHtml_StillRendersMarkdown()
    {
        var html = ProjectDocumentationService.ConvertToHtml("# Title\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");

        html.Should().Contain("<h1");
        html.Should().Contain("<table>");
    }

    [Test]
    public async Task DocumentationExport_IsAnAttachmentWithNoSniffing()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection()
            .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
            .BuildServiceProvider();

        var result = ProjectsEndpoints.ToDocumentationAttachment(
            context.Response,
            new ExportProjectDocumentationResult("<p>x</p>", "text/html", "documentation.html"));
        await result.ExecuteAsync(context);

        context.Response.Headers.XContentTypeOptions.ToString().Should().Be("nosniff");
        context.Response.Headers.ContentDisposition.ToString().Should().StartWith("attachment");
        context.Response.Headers.ContentDisposition.ToString().Should().Contain("documentation.html");
        context.Response.ContentType.Should().Be("text/html; charset=utf-8");
    }
}
