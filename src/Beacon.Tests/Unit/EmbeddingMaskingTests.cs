using FluentAssertions;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;

namespace Beacon.Tests.Unit;

[TestFixture]
public class EmbeddingMaskingTests
{
    [Test]
    public void Mask_NumericLiterals_Replaced()
    {
        EmbeddingMaskingHelper.Mask("Show top 10 customers")
            .Should().Be("show top <num> customers");
    }

    [Test]
    public void Mask_SingleQuotedLiteral_Replaced()
    {
        EmbeddingMaskingHelper.Mask("customers in 'New York'")
            .Should().Be("customers in <value>");
    }

    [Test]
    public void Mask_DoubleQuotedLiteral_Replaced()
    {
        EmbeddingMaskingHelper.Mask("customers in \"New York\"")
            .Should().Be("customers in <value>");
    }

    [Test]
    public void Mask_DateLikeToken_Replaced()
    {
        EmbeddingMaskingHelper.Mask("orders after 2023-05-01")
            .Should().Be("orders after <value>");
    }

    [Test]
    public void Mask_TwoQuestionsDifferingOnlyByLiterals_ProduceSameMask()
    {
        var first = EmbeddingMaskingHelper.Mask("How many orders over 500 were placed after 2023-05-01?");
        var second = EmbeddingMaskingHelper.Mask("How many orders over 999 were placed after 2020-12-31?");

        first.Should().Be(second);
        first.Should().Be("how many orders over <num> were placed after <value>?");
    }

    [Test]
    public void Mask_NormalizesCasingAndCollapsesWhitespace()
    {
        EmbeddingMaskingHelper.Mask("  SELECT   Foo   FROM   Bar  ")
            .Should().Be("select foo from bar");
    }

    [Test]
    public void Mask_NullOrEmpty_ReturnsEmpty()
    {
        EmbeddingMaskingHelper.Mask(null!).Should().BeEmpty();
        EmbeddingMaskingHelper.Mask(string.Empty).Should().BeEmpty();
    }
}
