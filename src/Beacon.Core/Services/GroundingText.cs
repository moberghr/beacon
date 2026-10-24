using System.Globalization;
using System.Text;

namespace Beacon.Core.Services;

/// <summary>
/// Text that grounds a prompt — a golden case's question and SQL, a learned pattern's example question and SQL — must read
/// the same to the model as to the person who reviewed it. Characters a review screen does not show are refused where such
/// text is stored and left out where it is rendered: format characters (Unicode category Cf — bidirectional overrides and
/// isolates, zero-width characters, the byte order mark), the tag characters U+E0000–U+E007F and the variation selectors
/// (U+FE00–U+FE0F, U+E0100–U+E01EF, U+180B–U+180F).
/// </summary>
public static class GroundingText
{
    /// <summary>True when <paramref name="text"/> holds a character a reviewer cannot see (see <see cref="GroundingText"/>).</summary>
    public static bool HasHiddenCharacters(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var rune in text.EnumerateRunes())
        {
            if (IsHidden(rune))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A question as a prompt shows it: on one line (line breaks and tabs become a space, so it cannot leave the line it is
    /// rendered on), trimmed. Null when it is empty or holds a hidden character, so it is left out.
    /// </summary>
    public static string? Question(string? question)
    {
        if (string.IsNullOrWhiteSpace(question) || HasHiddenCharacters(question))
        {
            return null;
        }

        var builder = new StringBuilder(question.Length);
        foreach (var character in question)
        {
            builder.Append(character is '\r' or '\n' or '\t' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029' ? ' ' : character);
        }

        return builder.ToString().Trim();
    }

    internal static bool IsHidden(Rune rune)
    {
        var value = rune.Value;

        return Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format
            || value is >= 0xE0000 and <= 0xE007F
            || value is >= 0xFE00 and <= 0xFE0F
            || value is >= 0xE0100 and <= 0xE01EF
            || value is >= 0x180B and <= 0x180F;
    }
}
