using Beacon.Core.Services.Embed;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// A grant's frame origins are exact https origins in lower case, without a path, wildcard or other part; the default
/// port is dropped; http://localhost only in Development; one to the configured maximum, distinct.
/// </summary>
[TestFixture]
public class EmbedOriginRulesTests
{
    [TestCase("https://portal.example.com", "https://portal.example.com")]
    [TestCase("  HTTPS://Portal.Example.COM ", "https://portal.example.com")]
    [TestCase("https://portal.example.com:443", "https://portal.example.com")]
    [TestCase("https://portal.example.com:8443", "https://portal.example.com:8443")]
    [TestCase("https://xn--bcher-kva.example", "https://xn--bcher-kva.example")]
    public void AnExactHttpsOrigin_IsAccepted_InItsNormalForm(string origin, string expected)
    {
        EmbedOriginRules.Normalize([origin], 10, allowLocalhost: false).Should().Equal(expected);
    }

    [TestCase("https://portal.example.com/")]
    [TestCase("https://portal.example.com/app")]
    [TestCase("https://portal.example.com?x=1")]
    [TestCase("https://portal.example.com#frag")]
    [TestCase("https://*.example.com")]
    [TestCase("*")]
    [TestCase("https://user@portal.example.com")]
    [TestCase("http://portal.example.com")]
    [TestCase("ftp://portal.example.com")]
    [TestCase("portal.example.com")]
    [TestCase("https://portal..example.com")]
    [TestCase("https://-portal.example.com")]
    [TestCase("https://portal.example.com:0")]
    [TestCase("https://portal.example.com:70000")]
    [TestCase("https://bücher.example")]
    [TestCase("'self'")]
    [TestCase("https://a.example https://b.example")]
    [TestCase("")]
    public void AnythingElse_IsRefused(string origin)
    {
        var act = () => EmbedOriginRules.Normalize([origin], 10, allowLocalhost: true);

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void LocalhostOverHttp_IsAcceptedOnlyInDevelopment()
    {
        EmbedOriginRules.Normalize(["http://localhost:5173"], 10, allowLocalhost: true).Should().Equal("http://localhost:5173");

        var act = () => EmbedOriginRules.Normalize(["http://localhost:5173"], 10, allowLocalhost: false);
        act.Should().Throw<InvalidOperationException>().WithMessage("*only in Development*");
        var other = () => EmbedOriginRules.Normalize(["http://127.0.0.1:5173"], 10, allowLocalhost: true);
        other.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Duplicates_CountOnce_AndTheCountIsBounded()
    {
        EmbedOriginRules.Normalize(["https://a.example", "https://A.example:443", "https://b.example"], 2, allowLocalhost: false)
            .Should().Equal("https://a.example", "https://b.example");

        var none = () => EmbedOriginRules.Normalize([], 2, allowLocalhost: false);
        none.Should().Throw<InvalidOperationException>().WithMessage("*at least one*");
        var tooMany = () => EmbedOriginRules.Normalize(["https://a.example", "https://b.example", "https://c.example"], 2, allowLocalhost: false);
        tooMany.Should().Throw<InvalidOperationException>().WithMessage("*at most 2*");
    }
}
