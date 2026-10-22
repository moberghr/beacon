using System.Text;

namespace Beacon.AI.Services.Documentation;

/// <summary>
/// Bounds the variable part of one documentation prompt to a character budget. Items (a table, a class, a term) are added
/// whole, in the order they have; one that does not fit is left out and counted (a later, smaller one may still fit), and
/// <see cref="AppendNote"/> says how many, without naming or listing any of them. Fixed text (the prompt's own headings)
/// is written straight to the builder; the reserve keeps it, and the note, inside the budget.
/// </summary>
internal sealed class DocumentationPromptBudget(StringBuilder sb, int limit = DocumentationPromptBudget.DefaultLimit)
{
    internal const int DefaultLimit = 100_000;
    internal const int Reserve = 2_000;

    private readonly StringBuilder _pending = new();
    private int _groupItems;

    public int Skipped { get; private set; }

    // A group's heading: written with the group's first item that fits, never alone.
    public void Heading(string text)
    {
        _groupItems = 0;
        _pending.Append(text);
    }

    public bool TryAdd(string block)
    {
        _groupItems++;
        if (sb.Length + _pending.Length + block.Length > limit - Reserve)
        {
            Skipped++;

            return false;
        }

        sb.Append(_pending).Append(block);
        _pending.Clear();

        return true;
    }

    // Closes a group. A group with items writes its trailer only when its heading was written; a group that had no items
    // at all writes its heading and trailer as it always did, while they fit.
    public void EndGroup(string trailer)
    {
        if (_pending.Length == 0)
        {
            sb.Append(trailer);
        }
        else if (_groupItems == 0 && sb.Length + _pending.Length + trailer.Length <= limit - Reserve)
        {
            sb.Append(_pending).Append(trailer);
        }

        _pending.Clear();
        _groupItems = 0;
    }

    public void AppendNote()
    {
        if (Skipped > 0)
        {
            sb.AppendLine($"+{Skipped} more not shown (prompt size limit)");
        }
    }
}
