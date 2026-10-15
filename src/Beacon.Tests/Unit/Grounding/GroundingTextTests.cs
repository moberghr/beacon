using Beacon.Core.Services;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Grounding;

/// <summary>
/// Text that grounds a prompt reads the same to the model as to its reviewer: characters a review screen does not show are
/// found by class — format characters (bidirectional overrides and isolates, zero-width characters, the byte order mark,
/// the soft hyphen), Unicode tag characters and variation selectors — while ordinary text, accents, other scripts and
/// emoji are not; a question renders on one line; what a curator reviewed hashes character for character.
/// </summary>
[TestFixture]
public class GroundingTextTests
{
    [TestCase("a\u202Eb", TestName = "Bidirectional override (U+202E)")]
    [TestCase("a\u202Ab", TestName = "Left-to-right embedding (U+202A)")]
    [TestCase("a\u2066b\u2069", TestName = "Isolates (U+2066, U+2069)")]
    [TestCase("a\u200Bb", TestName = "Zero-width space (U+200B)")]
    [TestCase("a\u200Cb", TestName = "Zero-width non-joiner (U+200C)")]
    [TestCase("a\u200Db", TestName = "Zero-width joiner (U+200D)")]
    [TestCase("a\u2060b", TestName = "Word joiner (U+2060)")]
    [TestCase("\uFEFFab", TestName = "Byte order mark (U+FEFF)")]
    [TestCase("a\u00ADb", TestName = "Soft hyphen (U+00AD)")]
    [TestCase("a\U000E0001b", TestName = "Language tag (U+E0001)")]
    [TestCase("a\U000E0041b", TestName = "Tag Latin capital A (U+E0041)")]
    [TestCase("a\U000E007Fb", TestName = "Cancel tag (U+E007F)")]
    [TestCase("a\uFE00b", TestName = "Variation selector 1 (U+FE00)")]
    [TestCase("a\uFE0Fb", TestName = "Variation selector 16 (U+FE0F)")]
    [TestCase("a\U000E0100b", TestName = "Variation selector 17 (U+E0100)")]
    [TestCase("a\U000E01EFb", TestName = "Variation selector 256 (U+E01EF)")]
    [TestCase("a\u180Bb", TestName = "Mongolian free variation selector (U+180B)")]
    public void ACharacterAReviewerCannotSee_IsFound(string text)
    {
        GroundingText.HasHiddenCharacters(text).Should().BeTrue();
        GroundingText.Question(text).Should().BeNull("a question holding one is left out");
    }

    [TestCase("How many orders were refunded?")]
    [TestCase("Koliko je narudžbi vraćeno?")]
    [TestCase("注文はいくつですか")]
    [TestCase("orders 😀 today")]
    [TestCase("tab\tand\nnewline")]
    [TestCase("")]
    public void OrdinaryText_HoldsNone(string text)
    {
        GroundingText.HasHiddenCharacters(text).Should().BeFalse();
    }

    [TestCase("orders\nlast week", "orders last week")]
    [TestCase("  orders\r\n-- last week\t", "orders  -- last week")]
    [TestCase("orders\u2028last\u2029week\u0085", "orders last week")]
    [TestCase("   ", null)]
    [TestCase(null, null)]
    public void AQuestion_RendersOnOneLine_Trimmed(string? question, string? rendered)
    {
        GroundingText.Question(question).Should().Be(rendered);
    }

    [Test]
    public void ReviewedSql_HashesCharacterForCharacter()
    {
        var hash = GroundingContentHash.OfSql("SELECT id FROM orders");

        hash.Should().MatchRegex("^[0-9a-f]{64}$");
        GroundingContentHash.OfSql("SELECT id FROM orders").Should().Be(hash);
        GroundingContentHash.OfSql("SELECT id FROM orders ").Should().NotBe(hash, "whitespace is content");
        GroundingContentHash.OfSql("SELECT id FROM orders\r\n").Should().NotBe(GroundingContentHash.OfSql("SELECT id FROM orders\n"));
        GroundingContentHash.Matches(hash.ToUpperInvariant(), hash).Should().BeTrue();
        GroundingContentHash.Matches(null, hash).Should().BeFalse();
        GroundingContentHash.Matches("", hash).Should().BeFalse();
    }

    [Test]
    public void AReviewedPattern_HashesItsLessonQuestionAndSql_TellingMissingFromEmpty()
    {
        var hash = GroundingContentHash.OfPattern("lesson", "question", "SELECT 1");

        GroundingContentHash.OfPattern("lesson", "question", "SELECT 1").Should().Be(hash);
        GroundingContentHash.OfPattern("lesson!", "question", "SELECT 1").Should().NotBe(hash);
        GroundingContentHash.OfPattern("lesson", "question?", "SELECT 1").Should().NotBe(hash);
        GroundingContentHash.OfPattern("lesson", "question", "SELECT 2").Should().NotBe(hash);
        GroundingContentHash.OfPattern("lesson", null, null).Should().NotBe(GroundingContentHash.OfPattern("lesson", "", ""));
        GroundingContentHash.OfPattern("a", "b", null).Should().NotBe(GroundingContentHash.OfPattern("a", null, "b"), "the parts do not run into one another");
    }
}
