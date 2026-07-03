using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Services;

namespace Beacon.Tests.Unit;

[TestFixture]
public class SqlIdentifierGuardTests
{
    [TestCase("orders")]
    [TestCase("Order_Items")]
    [TestCase("_private")]
    [TestCase("col123")]
    public void Validate_PlainIdentifier_ReturnsItUnchanged(string identifier)
    {
        SqlIdentifierGuard.Validate(identifier, "column").Should().Be(identifier);
    }

    // §1.10 — anything that is not a plain identifier must be rejected before it reaches SQL text.
    [TestCase("")]
    [TestCase("1col")]
    [TestCase("col name")]
    [TestCase("col-name")]
    [TestCase("col\"; DROP TABLE users;--")]
    [TestCase("col')")]
    [TestCase("a.b")]
    public void Validate_NonIdentifier_Throws(string identifier)
    {
        var act = () => SqlIdentifierGuard.Validate(identifier, "column");

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void EscapeQuote_DoublesTheTargetQuoteCharacter()
    {
        SqlIdentifierGuard.EscapeQuote("a\"b\"c", '"').Should().Be("a\"\"b\"\"c");
        SqlIdentifierGuard.EscapeQuote("a`b", '`').Should().Be("a``b");
        SqlIdentifierGuard.EscapeQuote("plain", '"').Should().Be("plain");
    }
}
