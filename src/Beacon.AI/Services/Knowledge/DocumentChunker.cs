using System.Text.RegularExpressions;

namespace Beacon.AI.Services.Knowledge;

/// <summary>
/// Pure, deterministic sentence-window chunker for project documentation (Tier-3 ⑨). Splits content
/// into sentences (terminator + whitespace, plus blank lines as hard breaks) and emits a sliding
/// window of <paramref name="windowSentences"/> sentences with <paramref name="overlapSentences"/>
/// overlap. No I/O, no randomness — the same input always yields the same chunks so indexing is
/// idempotent and unit-testable in isolation.
/// </summary>
internal static class DocumentChunker
{
    // Split at whitespace that follows a sentence terminator, keeping the terminator on the left
    // sentence via a look-behind. A single newline after a terminator (wrapped paragraph) also splits.
    private static readonly Regex SentenceSplit = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);

    // Blank line / double newline = hard paragraph break. Line endings are normalized to \n first.
    private static readonly Regex ParagraphSplit = new(@"\n\s*\n", RegexOptions.Compiled);

    public static IReadOnlyList<string> Chunk(string content, int windowSentences, int overlapSentences)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var sentences = SplitSentences(content);
        if (sentences.Count == 0)
        {
            return [];
        }

        // Clamp so the window is always at least one sentence and the step is always positive:
        // overlap is forced below the window (overlap >= window collapses to window - 1), so
        // step = window - overlap >= 1 and the sliding loop can never spin forever.
        var window = Math.Max(1, windowSentences);
        var overlap = Math.Clamp(overlapSentences, 0, window - 1);
        var step = window - overlap;

        var chunks = new List<string>();
        var start = 0;
        while (start < sentences.Count)
        {
            var take = Math.Min(window, sentences.Count - start);
            chunks.Add(string.Join(" ", sentences.GetRange(start, take)));

            // Once a window reaches the tail, that window already contains every remaining sentence —
            // stop so short input (or the final window) yields a single trailing chunk, not sub-windows.
            if (start + window >= sentences.Count)
            {
                break;
            }

            start += step;
        }

        return chunks;
    }

    private static List<string> SplitSentences(string content)
    {
        var normalized = content
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");

        var sentences = new List<string>();
        foreach (var paragraph in ParagraphSplit.Split(normalized))
        {
            if (string.IsNullOrWhiteSpace(paragraph))
            {
                continue;
            }

            foreach (var part in SentenceSplit.Split(paragraph))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    sentences.Add(trimmed);
                }
            }
        }

        return sentences;
    }
}
