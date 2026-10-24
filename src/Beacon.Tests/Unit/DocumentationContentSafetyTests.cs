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

    [TestCase("# h {onclick=alert(1)}", "onclick")]
    [TestCase("![x](a.png){onerror=alert(1)}", "onerror")]
    [TestCase("text {onmouseover=alert(1)}", "onmouseover")]
    public void ConvertToHtml_DropsGenericAttributes(string markdown, string attribute)
    {
        var html = ProjectDocumentationService.ConvertToHtml(markdown);

        var body = html[html.IndexOf("<body>", StringComparison.Ordinal)..];
        body.Should().NotMatchRegex("<[^>]*\\b" + attribute + "=");
    }

    [TestCase("[x](javascript:alert(1))")]
    [TestCase("[x](JaVaScRiPt:alert(1))")]
    [TestCase("[x](vbscript:msgbox(1))")]
    [TestCase("[x](data:text/html,hi)")]
    [TestCase("![i](javascript:alert(1))")]
    [TestCase("<javascript:alert(1)>")]
    [TestCase("[r]\n\n[r]: javascript:alert(1)")]
    [TestCase("[x](java&#x09;script:alert(1))")]
    [TestCase("[x](//example.com/a)")]
    public void ConvertToHtml_DropsUnsafeUrlSchemes(string markdown)
    {
        var html = ProjectDocumentationService.ConvertToHtml(markdown);

        var body = html[html.IndexOf("<body>", StringComparison.Ordinal)..];
        body.ToLowerInvariant().Should().NotMatchRegex("(href|src)=\"(javascript|vbscript|data):");
        body.Should().NotContain("href=\"//");
    }

    [TestCase("[x](https://example.com/a)", "href=\"https://example.com/a\"")]
    [TestCase("[x](http://example.com/a)", "href=\"http://example.com/a\"")]
    [TestCase("[x](mailto:a@example.com)", "href=\"mailto:a@example.com\"")]
    [TestCase("[x](#section)", "href=\"#section\"")]
    [TestCase("[x](/projects/1)", "href=\"/projects/1\"")]
    [TestCase("[x](page.html)", "href=\"page.html\"")]
    [TestCase("![i](https://example.com/a.png)", "src=\"https://example.com/a.png\"")]
    public void ConvertToHtml_KeepsAllowedUrls(string markdown, string expected)
    {
        var html = ProjectDocumentationService.ConvertToHtml(markdown);

        html.Should().Contain(expected);
    }

    [Test]
    public void ConvertToHtml_DeclaresContentSecurityPolicy()
    {
        var html = ProjectDocumentationService.ConvertToHtml("# Title");

        var head = html[..html.IndexOf("</head>", StringComparison.Ordinal)];
        head.Should().Contain("http-equiv=\"Content-Security-Policy\"");
        head.Should().Contain("default-src 'none'");
    }

    [Test]
    public void ConvertToHtml_StillRendersMarkdown()
    {
        var html = ProjectDocumentationService.ConvertToHtml("# Title\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");

        html.Should().Contain("<h1");
        html.Should().Contain("<table>");
    }

    [Test]
    public void ConvertToHtml_StillRendersTaskListsAndAutolinks()
    {
        var html = ProjectDocumentationService.ConvertToHtml("- [x] done\n\nsee https://example.com now\n");

        html.Should().Contain("type=\"checkbox\"");
        html.Should().Contain("href=\"https://example.com\"");
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
