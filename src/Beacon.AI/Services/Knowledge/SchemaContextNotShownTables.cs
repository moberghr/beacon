namespace Beacon.AI.Services.Knowledge;

/// <summary>
/// The tables a statement reads that its ask schema context left out (<see cref="SmartSchemaContext.TablesNotShown"/>): a
/// repair of that statement gets their exact schema, as the model never saw them.
/// </summary>
public static class SchemaContextNotShownTables
{
    /// <summary>
    /// The exact schemas of the tables in <paramref name="tablesUsed"/> that <paramref name="context"/> does not show; null
    /// when the statement reads none of them.
    /// </summary>
    public static async Task<string?> GetNotShownTablesContextAsync(
        this IKnowledgeGraphService knowledgeGraph,
        int dataSourceId,
        int projectId,
        SmartSchemaContext context,
        IEnumerable<string> tablesUsed,
        CancellationToken ct)
    {
        var notShown = NotShownTablesIn(tablesUsed, context.TablesNotShown);

        return notShown.Count > 0
            ? await knowledgeGraph.GetTablesContextAsync(dataSourceId, projectId, notShown, ct)
            : null;
    }

    /// <summary>
    /// The names in <paramref name="notShown"/> (<c>schema.table</c>, or the table name alone on an API source) that a name
    /// in <paramref name="tablesUsed"/> refers to, as <paramref name="notShown"/> spells them. A used name may be quoted
    /// (<c>"s"."t"</c>, <c>[s].[t]</c>, <c>`s`.`t`</c>), unqualified, or carry a database or project in front
    /// (<c>db.schema.table</c>): an unqualified name, or a name on an API source, is compared by table name; a qualified one
    /// by its last two parts.
    /// </summary>
    public static IReadOnlyList<string> NotShownTablesIn(IEnumerable<string> tablesUsed, IReadOnlyList<string> notShown)
    {
        var matched = new List<string>();
        if (notShown.Count == 0)
        {
            return matched;
        }

        var notShownParts = notShown
            .Select(x => (Name: x, Parts: x.Split('.')))
            .ToList();
        foreach (var used in tablesUsed)
        {
            var usedParts = Unquoted(used).Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (usedParts.Length == 0)
            {
                continue;
            }

            foreach (var (name, parts) in notShownParts)
            {
                if (RefersTo(usedParts, parts) && !matched.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    matched.Add(name);
                }
            }
        }

        return matched;
    }

    private static bool RefersTo(string[] used, string[] notShown)
    {
        if (!used[^1].Equals(notShown[^1], StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return used.Length == 1
            || notShown.Length == 1
            || used[^2].Equals(notShown[^2], StringComparison.OrdinalIgnoreCase);
    }

    private static string Unquoted(string name)
    {
        return name
            .Replace("\"", "")
            .Replace("`", "")
            .Replace("[", "")
            .Replace("]", "");
    }
}
