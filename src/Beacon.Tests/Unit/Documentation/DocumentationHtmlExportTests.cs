using Beacon.AI.Services.Documentation;
using Beacon.Api.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Documentation;

/// <summary>
/// The HTML export is a file a reader opens: section text (generated, hand-edited or proposed) never reaches it as live
/// markup, and the response itself forbids running anything.
/// </summary>
[TestFixture]
public class DocumentationHtmlExportTests
{
    [Test]
    public void ConvertToHtml_EscapesRawHtmlInSections()
    {
        var markdown = "# Orders\n\n<script>alert(1)</script>\n\nText <img src=x onerror=alert(2)> more\n\n<div onclick=\"x()\">block</div>";

        var html = ProjectDocumentationService.ConvertToHtml(markdown);

        html.Should().NotContain("<script>alert");
        html.Should().NotContain("<img src=x");
        html.Should().NotContain("<div onclick");
        html.Should().Contain("&lt;script&gt;");
        html.Should().Contain("&lt;img");
    }

    [Test]
    public void ConvertToHtml_StillRendersMarkdown()
    {
        var html = ProjectDocumentationService.ConvertToHtml("# Orders\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n**bold**");

        html.Should().Contain("<h1");
        html.Should().Contain("<table>");
        html.Should().Contain("<strong>bold</strong>");
    }

    [Test]
    public void ApplyExportHeaders_SandboxesTheFileAndForbidsSniffing()
    {
        var context = new DefaultHttpContext();

        ProjectsEndpoints.ApplyExportHeaders(context.Response);

        context.Response.Headers["Content-Security-Policy"].ToString().Should().Be("sandbox");
        context.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
    }
}
