using FluentAssertions;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;

namespace Beacon.Tests.Unit;

[TestFixture]
public class DocumentChunkerTests
{
    // Ten single-token sentences ("S1." .. "S10.") so window boundaries are trivial to reason about.
    private const string TenSentences = "S1. S2. S3. S4. S5. S6. S7. S8. S9. S10.";

    [Test]
    public void Chunk_WindowFiveOverlapOne_StepsByFour()
    {
        // 10 sentences, window 5, overlap 1 => step 4 => windows starting at sentence 1, 5, 9.
        var chunks = DocumentChunker.Chunk(TenSentences, windowSentences: 5, overlapSentences: 1);

        chunks.Should().HaveCount(3);
        chunks[0].Should().Be("S1. S2. S3. S4. S5.");
        chunks[1].Should().Be("S5. S6. S7. S8. S9.");
        // Final window is shorter than the window size — it only holds the trailing sentences.
        chunks[2].Should().Be("S9. S10.");
    }

    [Test]
    public void Chunk_Overlap_ConsecutiveChunksShareOverlapSentences()
    {
        var chunks = DocumentChunker.Chunk(TenSentences, windowSentences: 4, overlapSentences: 2);

        // window 4, overlap 2 => step 2 => starts at 1, 3, 5, 7 (indices 0,2,4,6); tail window 7..10.
        chunks.Should().HaveCount(4);
        chunks[0].Should().Be("S1. S2. S3. S4.");
        chunks[1].Should().Be("S3. S4. S5. S6.");
        chunks[2].Should().Be("S5. S6. S7. S8.");
        chunks[3].Should().Be("S7. S8. S9. S10.");

        // Each consecutive pair shares exactly the 2 overlap sentences (end of one == start of next).
        chunks[0].Should().EndWith("S3. S4.");
        chunks[1].Should().StartWith("S3. S4.");
    }

    [Test]
    public void Chunk_EmptyString_ReturnsEmptyList()
    {
        DocumentChunker.Chunk(string.Empty, 5, 1).Should().BeEmpty();
    }

    [Test]
    public void Chunk_WhitespaceOnly_ReturnsEmptyList()
    {
        DocumentChunker.Chunk("   \n\n \t  ", 5, 1).Should().BeEmpty();
    }

    [Test]
    public void Chunk_NullContent_ReturnsEmptyList()
    {
        DocumentChunker.Chunk(null!, 5, 1).Should().BeEmpty();
    }

    [Test]
    public void Chunk_ContentShorterThanWindow_ReturnsSingleChunkWithAllSentences()
    {
        var chunks = DocumentChunker.Chunk("Only one. Only two.", windowSentences: 5, overlapSentences: 1);

        chunks.Should().ContainSingle();
        chunks[0].Should().Be("Only one. Only two.");
    }

    [Test]
    public void Chunk_OverlapGreaterThanOrEqualWindow_TreatedAsWindowMinusOne_NoInfiniteLoop()
    {
        // overlap (10) >= window (3) must collapse to overlap = window - 1 = 2 => step 1, not a hang.
        var chunks = DocumentChunker.Chunk(TenSentences, windowSentences: 3, overlapSentences: 10);

        // step 1 over 10 sentences with a 3-sentence window => 8 windows (last is 8..10).
        chunks.Should().HaveCount(8);
        chunks[0].Should().Be("S1. S2. S3.");
        chunks[1].Should().Be("S2. S3. S4.");
        chunks[^1].Should().Be("S8. S9. S10.");
    }

    [Test]
    public void Chunk_WindowLessThanOne_ClampedToOneSentencePerChunk()
    {
        var chunks = DocumentChunker.Chunk("A. B. C.", windowSentences: 0, overlapSentences: 0);

        chunks.Should().Equal("A.", "B.", "C.");
    }

    [Test]
    public void Chunk_AllTerminators_SplitIntoSeparateSentences()
    {
        var chunks = DocumentChunker.Chunk("Q one? Excl two! Period three.", windowSentences: 1, overlapSentences: 0);

        chunks.Should().Equal("Q one?", "Excl two!", "Period three.");
    }

    [Test]
    public void Chunk_BlankLine_IsHardBreakEvenWithoutTerminator()
    {
        // No sentence terminators anywhere — only the blank line separates the two sentences.
        var chunks = DocumentChunker.Chunk("alpha beta\n\ngamma delta", windowSentences: 1, overlapSentences: 0);

        chunks.Should().Equal("alpha beta", "gamma delta");
    }

    [Test]
    public void Chunk_CarriageReturnNewlines_NormalizedAsBlankBreak()
    {
        var chunks = DocumentChunker.Chunk("first line.\r\n\r\nsecond line.", windowSentences: 1, overlapSentences: 0);

        chunks.Should().Equal("first line.", "second line.");
    }

    [Test]
    public void Chunk_Deterministic_SameInputProducesIdenticalOutput()
    {
        var first = DocumentChunker.Chunk(TenSentences, windowSentences: 4, overlapSentences: 1);
        var second = DocumentChunker.Chunk(TenSentences, windowSentences: 4, overlapSentences: 1);

        first.Should().Equal(second);
    }
}
