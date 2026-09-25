using Beacon.Core.HostDocs;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.HostDocs;

[TestFixture]
public class HostDocumentParserTests
{
    // Shape of a docu-me generated wiki page (Netgiro docs/wiki).
    private const string DocuMePage = """
        ---
        sources:
          - Netgiro.Web.Admin/**
          - Netgiro.Services.Admin/**
        title: Web.Admin
        ---

        # Web.Admin

        The internal back office.
        """;

    [Test]
    public void Frontmatter_BlockListAndTitle_AreParsed_AndStrippedFromBody()
    {
        var parsed = HostDocumentParser.Parse("40-web/admin.md", DocuMePage);

        parsed.Title.Should().Be("Web.Admin");
        parsed.Body.Should().StartWith("# Web.Admin");
        parsed.Body.Should().NotContain("sources:");
        parsed.FrontmatterJson.Should().Be("""{"sources":["Netgiro.Web.Admin/**","Netgiro.Services.Admin/**"],"title":"Web.Admin"}""");
    }

    [Test]
    public void Frontmatter_QuotedScalarsInlineListsAndComments()
    {
        const string raw = "---\n# generated\ntitle: \"Checkout: portals\"\ntags: [web, 'checkout']\nowner: 'team-a'\nnested:\n  key: value\n---\nBody.";

        var parsed = HostDocumentParser.Parse("x.md", raw);

        parsed.Title.Should().Be("Checkout: portals");
        parsed.FrontmatterJson.Should().Be("""{"title":"Checkout: portals","tags":["web","checkout"],"owner":"team-a","nested":null}""");
        parsed.Body.Should().Be("Body.");
    }

    [Test]
    public void Title_FallsBackToFirstHeading_OutsideCodeFences()
    {
        const string raw = "Intro line.\n\n```md\n# Not a title\n```\n\n# Real Title\n\nText.";

        HostDocumentParser.Parse("docs/page.md", raw).Title.Should().Be("Real Title");
    }

    [Test]
    public void Title_FallsBackToFileName_WhenNoFrontmatterTitleOrHeading()
    {
        var parsed = HostDocumentParser.Parse("80-conventions/dev-tooling.md", "## Only a level-two heading\n\nText.");

        parsed.Title.Should().Be("dev-tooling");
        parsed.FrontmatterJson.Should().BeNull();
    }

    [Test]
    public void Title_EmptyFrontmatterTitle_FallsBackToHeading()
    {
        HostDocumentParser.Parse("a.md", "---\ntitle: \n---\n# Heading").Title.Should().Be("Heading");
    }

    [Test]
    public void UnterminatedFrontmatter_IsKeptAsBody()
    {
        const string raw = "---\ntitle: Broken\n\n# Heading";

        var parsed = HostDocumentParser.Parse("a.md", raw);

        parsed.FrontmatterJson.Should().BeNull();
        parsed.Body.Should().Be(raw);
        parsed.Title.Should().Be("Heading");
    }

    [Test]
    public void CrLfAndBom_AreNormalized()
    {
        var parsed = HostDocumentParser.Parse("a.md", "﻿---\r\ntitle: Windows\r\n---\r\nLine one.\r\nLine two.");

        parsed.Title.Should().Be("Windows");
        parsed.Body.Should().Be("Line one.\nLine two.");
    }

    [Test]
    public void LongTitle_IsCappedAtMaxTitleLength()
    {
        var parsed = HostDocumentParser.Parse("a.md", "# " + new string('t', 800));

        parsed.Title.Length.Should().Be(HostDocumentParser.MaxTitleLength);
    }
}
