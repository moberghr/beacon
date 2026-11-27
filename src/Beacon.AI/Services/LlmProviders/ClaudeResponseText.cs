namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// The answer in a Claude response's content blocks: the text of its <c>text</c> blocks, in order. A model that thinks
/// first answers with a <c>thinking</c> block before the text, so the first block is not the answer; thinking and any
/// other block type are left out.
/// </summary>
public static class ClaudeResponseText
{
    public static string Of(IEnumerable<(string? Type, string? Text)>? blocks)
    {
        if (blocks == null)
        {
            return string.Empty;
        }

        var texts = blocks
            .Where(x => string.Equals(x.Type, "text", StringComparison.Ordinal))
            .Where(x => x.Text != null)
            .Select(x => x.Text);

        return string.Concat(texts);
    }
}
