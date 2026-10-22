using System.Text;
using System.Text.RegularExpressions;

namespace Beacon.AI.Services.Knowledge;

/// <summary>
/// One table's rendered description in an ask schema context. <see cref="Name"/> is how the context names it
/// (<see cref="AskSchemaContextBudget.BlockName"/>).
/// </summary>
internal sealed record SchemaContextTableBlock(string Name, string SchemaName, string TableName, string Text);

/// <summary>
/// A run of table blocks under one heading. A section whose heading is not <see cref="HeadingWhenEmpty"/> renders nothing
/// when none of its blocks is shown.
/// </summary>
internal sealed record SchemaContextSection(string Heading, IReadOnlyList<SchemaContextTableBlock> Blocks, bool HeadingWhenEmpty);

/// <summary>The grounding sections an ask schema context carries after its table blocks.</summary>
internal enum SchemaContextPartKind
{
    JoinPaths,
    Coverage,
    ValueMatches,
    GoldenExemplars,
    LearnedPatterns,
    Glossary
}

/// <summary>One grounding section after the table blocks, rendered in the order given.</summary>
internal sealed record SchemaContextPart(SchemaContextPartKind Kind, string Text);

internal sealed record BudgetedSchemaContext(string Text, int TablesShown, IReadOnlyList<string> TablesNotShown)
{
    /// <summary>Grounding sections left out so the most relevant table fits; empty under the budget.</summary>
    public IReadOnlyList<SchemaContextPartKind> PartsLeftOut { get; init; } = [];

    /// <summary>
    /// No whole table fit even without the optional grounding sections: the most relevant one is shown cut at a line
    /// boundary, followed by a line saying more columns are not shown.
    /// </summary>
    public bool TopTableCut { get; init; }
}

/// <summary>
/// Bounds the schema context an <c>ask</c> sends to the LLM (<c>Beacon:Mcp:Ceilings:AskSchemaContextChars</c>). Under the
/// budget the context is rendered exactly as without one. Over it, whole table blocks are kept in rank order — the names
/// the caller ranks first, then the existing order — skipping each block that would not fit, and one line names the tables
/// left out, so the model knows they exist; that line is counted inside the budget. When the grounding sections leave no
/// room for the most relevant table they are left out one kind at a time (<see cref="DropOrder"/>). A block is never forced
/// in whole: when no whole table fits, the most relevant one is shown cut at the last line that fits, followed by a line
/// saying more columns are not shown, so a context never shows no table when the source has one and is never longer than
/// the budget. When tables are left out the
/// coverage note is left out too, so the line naming them is the one statement of what the context does not show.
/// </summary>
internal static partial class AskSchemaContextBudget
{
    // Longest list of omitted table names the tail line carries before it ends with an ellipsis.
    public const int OmittedNamesMaxChars = 2_000;

    /// <summary>The order grounding sections are left out in when the most relevant table does not fit.</summary>
    public static readonly IReadOnlyList<SchemaContextPartKind> DropOrder =
    [
        SchemaContextPartKind.LearnedPatterns,
        SchemaContextPartKind.Glossary,
        SchemaContextPartKind.GoldenExemplars,
        SchemaContextPartKind.ValueMatches,
        SchemaContextPartKind.JoinPaths
    ];

    /// <summary>How a context names a table: <c>schema.table</c>, or the table name alone on an API source.</summary>
    public static string BlockName(string schemaName, string tableName, bool isApi)
    {
        return isApi ? tableName : $"{schemaName}.{tableName}";
    }

    public static BudgetedSchemaContext Assemble(
        string head,
        IReadOnlyList<SchemaContextSection> sections,
        IReadOnlyList<SchemaContextPart> parts,
        IReadOnlyList<string> rankedNames,
        int budgetChars)
    {
        var blocks = sections
            .SelectMany(x => x.Blocks)
            .ToList();
        var everything = Render(head, sections, parts, _ => true, null);
        if (everything.Length <= budgetChars)
        {
            return new BudgetedSchemaContext(everything, blocks.Count, []);
        }

        var order = RankedOrder(blocks, rankedNames);

        // Headings count as if every one is rendered, so leaving a heading out never pushes the context over the budget; the
        // line naming the tables left out is reserved at its longest.
        var keptParts = parts.ToList();
        var remaining = budgetChars
            - head.Length
            - sections.Sum(x => x.Heading.Length)
            - OmittedTablesLineMaxChars(blocks.Count)
            - keptParts.Sum(x => x.Text.Length);

        var partsLeftOut = new List<SchemaContextPartKind>();
        var top = order.FirstOrDefault();
        foreach (var kind in DropOrder)
        {
            if (top == null || top.Text.Length <= remaining)
            {
                break;
            }

            var leftOut = keptParts
                .Where(x => x.Kind == kind)
                .ToList();
            if (leftOut.Count == 0)
            {
                continue;
            }

            keptParts.RemoveAll(x => x.Kind == kind);
            remaining += leftOut.Sum(x => x.Text.Length);
            partsLeftOut.Add(kind);
        }

        var shown = new HashSet<SchemaContextTableBlock>(ReferenceEqualityComparer.Instance);

        // A block that does not fit is skipped, not a stop: a shorter one ranked after it may still fit.
        foreach (var block in order)
        {
            if (block.Text.Length > remaining)
            {
                continue;
            }

            shown.Add(block);
            remaining -= block.Text.Length;
        }

        var topTableCut = false;
        var renderedSections = sections;
        if (shown.Count == 0 && top != null)
        {
            var cut = CutBlock(top, remaining);
            shown.Add(cut);
            topTableCut = true;
            renderedSections = sections
                .Select(x =>
                    x with
                    {
                        Blocks = x.Blocks
                            .Select(y => ReferenceEquals(y, top) ? cut : y)
                            .ToList()
                    })
                .ToList();
            order = order
                .Select(x => ReferenceEquals(x, top) ? cut : x)
                .ToList();
        }

        var notShown = order
            .Where(x => !shown.Contains(x))
            .Select(x => x.Name)
            .ToList();
        if (notShown.Count > 0 || topTableCut)
        {
            keptParts.RemoveAll(x => x.Kind == SchemaContextPartKind.Coverage);
        }

        var text = Render(head, renderedSections, keptParts, shown.Contains, notShown.Count > 0 ? OmittedTablesLine(notShown) : null);

        return new BudgetedSchemaContext(text, shown.Count, notShown)
        {
            PartsLeftOut = partsLeftOut,
            TopTableCut = topTableCut
        };
    }

    /// <summary>
    /// The names of the blocks the <paramref name="question"/> names as a whole word, case-insensitively, in block order. A
    /// qualified name (<c>schema.table</c>, or longer) names only the table in that schema; when no block has that schema and
    /// table, each of its parts counts as an unqualified word (<c>orders.total</c> names <c>orders</c>).
    /// </summary>
    public static IReadOnlyList<string> TablesNamedIn(string question, IReadOnlyList<SchemaContextTableBlock> blocks)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var qualified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var schemaTables = blocks
            .Select(x => $"{x.SchemaName}.{x.TableName}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var token in NameRegex().Matches(question).Select(x => x.Value))
        {
            var parts = token.Split('.');
            if (parts.Length == 1)
            {
                words.Add(token);
                continue;
            }

            var schemaTable = $"{parts[^2]}.{parts[^1]}";
            if (schemaTables.Contains(schemaTable))
            {
                qualified.Add(schemaTable);
                continue;
            }

            words.UnionWith(parts);
        }

        return blocks
            .Where(x => words.Contains(x.TableName) || qualified.Contains($"{x.SchemaName}.{x.TableName}"))
            .Select(x => x.Name)
            .ToList();
    }

    public static string OmittedTablesLine(IReadOnlyList<string> names)
    {
        var listed = new StringBuilder();
        foreach (var name in names)
        {
            var separator = listed.Length == 0 ? "" : ", ";
            if (listed.Length + separator.Length + name.Length > OmittedNamesMaxChars)
            {
                listed.Append(separator).Append('…');
                break;
            }

            listed.Append(separator).Append(name);
        }

        return $"-- {names.Count} more tables not shown: {listed}";
    }

    /// <summary>The longest line <see cref="OmittedTablesLine"/> writes for up to this many names, with its line break.</summary>
    public static int OmittedTablesLineMaxChars(int tableCount)
    {
        return $"-- {tableCount} more tables not shown: ".Length + OmittedNamesMaxChars + ", …".Length + Environment.NewLine.Length;
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+(?:\.[\p{L}\p{N}_]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    private static SchemaContextTableBlock CutBlock(SchemaContextTableBlock block, int room)
    {
        var marker = $"-- {block.Name}: more columns not shown{Environment.NewLine}";
        var fits = Math.Max(0, Math.Min(block.Text.Length, room - marker.Length));
        var lineEnd = block.Text.LastIndexOf('\n', Math.Max(0, fits - 1), fits);
        var kept = lineEnd < 0 ? "" : block.Text[..(lineEnd + 1)];

        return block with { Text = kept + marker };
    }

    private static List<SchemaContextTableBlock> RankedOrder(List<SchemaContextTableBlock> blocks, IReadOnlyList<string> rankedNames)
    {
        var byName = blocks
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);

        var order = new List<SchemaContextTableBlock>(blocks.Count);
        var placed = new HashSet<SchemaContextTableBlock>(ReferenceEqualityComparer.Instance);
        foreach (var name in rankedNames)
        {
            if (!byName.TryGetValue(name, out var named))
            {
                continue;
            }

            foreach (var block in named)
            {
                if (placed.Add(block))
                {
                    order.Add(block);
                }
            }
        }

        foreach (var block in blocks)
        {
            if (placed.Add(block))
            {
                order.Add(block);
            }
        }

        return order;
    }

    private static string Render(
        string head,
        IReadOnlyList<SchemaContextSection> sections,
        IReadOnlyList<SchemaContextPart> parts,
        Func<SchemaContextTableBlock, bool> isShown,
        string? omittedLine)
    {
        var sb = new StringBuilder(head);
        foreach (var section in sections)
        {
            var shownBlocks = section.Blocks
                .Where(isShown)
                .ToList();
            if (shownBlocks.Count == 0 && !section.HeadingWhenEmpty)
            {
                continue;
            }

            sb.Append(section.Heading);
            foreach (var block in shownBlocks)
            {
                sb.Append(block.Text);
            }
        }

        if (omittedLine != null)
        {
            sb.AppendLine(omittedLine);
        }

        foreach (var part in parts)
        {
            sb.Append(part.Text);
        }

        return sb.ToString();
    }
}
