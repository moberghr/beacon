namespace Beacon.AI.Services.Knowledge;

/// <summary>
/// Whole-word matching of glossary terms in a question, for the glossary without semantic retrieval: a term or one of its
/// synonyms (separated by commas or semicolons) occurs in the question as a whole word, compared case-insensitively and
/// culture-invariant (ordinal, ignoring case). A linear scan per name, no regular expression. A name without a letter or
/// digit names nothing.
/// </summary>
internal static class GlossaryKeywords
{
    private static readonly char[] SynonymSeparators = [',', ';'];

    /// <summary>The length of the longest of the term and its synonyms the question names as a whole word; 0 when none.</summary>
    public static int LongestMatch(string question, string term, string? synonyms)
    {
        var longest = 0;
        foreach (var name in Names(term, synonyms))
        {
            if (name.Length > longest && NamesWholeWord(question, name))
            {
                longest = name.Length;
            }
        }

        return longest;
    }

    /// <summary>Whether <paramref name="name"/> occurs in <paramref name="question"/> with no word character joined to it.</summary>
    public static bool NamesWholeWord(string question, string name)
    {
        if (name.Length == 0 || !name.Any(char.IsLetterOrDigit))
        {
            return false;
        }

        var start = question.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        while (start >= 0)
        {
            if (Bounded(question, start - 1, name[0]) && Bounded(question, start + name.Length, name[^1]))
            {
                return true;
            }

            start = question.IndexOf(name, start + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    // A name that starts (or ends) with a word character needs the question's edge or a character that is not one next to it.
    private static bool Bounded(string question, int index, char edge)
    {
        return !IsWordCharacter(edge) || index < 0 || index >= question.Length || !IsWordCharacter(question[index]);
    }

    private static bool IsWordCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character == '_';
    }

    private static IEnumerable<string> Names(string term, string? synonyms)
    {
        var trimmed = term.Trim();
        if (trimmed.Length > 0)
        {
            yield return trimmed;
        }

        if (string.IsNullOrWhiteSpace(synonyms))
        {
            yield break;
        }

        foreach (var synonym in synonyms.Split(SynonymSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            yield return synonym;
        }
    }
}
