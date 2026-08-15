using Beacon.Core.Models.Metadata;

namespace Beacon.Core.Services.Metadata;

/// <summary>
/// An immutable adjacency view of one data source's tables and registered relationships, with the
/// bounded traversal primitives the prompt builder and the schema-health report need. Pure — built from
/// already-loaded metadata, so every behaviour here is testable without a database.
/// </summary>
/// <remarks>
/// Adapted from pgGraph's traversal contract: bounded depth, a visited set, a frontier cap, and an
/// explicit capped flag so a truncated result is never mistaken for a complete one. The deliberate
/// divergence is junction handling — pgGraph collapses link tables out of the graph, while Beacon keeps
/// them as zero-cost nodes because they must appear in the generated SQL.
/// </remarks>
public sealed class SchemaGraph
{
    private const int DefaultMaxDepth = 3;
    private const int FrontierLimit = 4096;

    private readonly Dictionary<string, SchemaGraphNode> _nodes;
    private readonly Dictionary<string, List<SchemaJoinStep>> _adjacency;

    private SchemaGraph(
        Dictionary<string, SchemaGraphNode> nodes,
        Dictionary<string, List<SchemaJoinStep>> adjacency)
    {
        _nodes = nodes;
        _adjacency = adjacency;
    }

    public IReadOnlyCollection<SchemaGraphNode> Nodes => _nodes.Values;

    public int EdgeCount { get; private init; }

    /// <summary>
    /// Builds the graph. Relationships whose endpoints are not both present in <paramref name="tables"/>
    /// are dropped rather than kept as dangling edges — a table can be removed from the source between
    /// metadata refreshes while its relationship rows survive.
    /// </summary>
    public static SchemaGraph Build(
        IReadOnlyList<TableMetadataDto> tables,
        IReadOnlyList<SchemaRelationshipEdge> relationships)
    {
        var nodes = new Dictionary<string, SchemaGraphNode>(StringComparer.OrdinalIgnoreCase);
        var adjacency = new Dictionary<string, List<SchemaJoinStep>>(StringComparer.OrdinalIgnoreCase);

        var liveEdges = relationships
            .Where(x => ContainsTable(tables, x.SourceSchema, x.SourceTable))
            .Where(x => ContainsTable(tables, x.TargetSchema, x.TargetTable))
            .ToList();

        foreach (var table in tables)
        {
            var key = Qualify(table.SchemaName, table.TableName);
            nodes[key] = new SchemaGraphNode(table.SchemaName, table.TableName, IsJunction(table, liveEdges));
            adjacency[key] = [];
        }

        foreach (var edge in liveEdges)
        {
            var from = Qualify(edge.SourceSchema, edge.SourceTable);
            var to = Qualify(edge.TargetSchema, edge.TargetTable);

            adjacency[from].Add(new SchemaJoinStep(
                from, edge.SourceColumn, to, edge.TargetColumn,
                edge.Label, edge.Origin, edge.IsVerified, edge.Confidence, nodes[to].IsJunction));

            // A join reads in both directions, so the graph is undirected for path purposes.
            adjacency[to].Add(new SchemaJoinStep(
                to, edge.TargetColumn, from, edge.SourceColumn,
                edge.Label, edge.Origin, edge.IsVerified, edge.Confidence, nodes[from].IsJunction));
        }

        return new SchemaGraph(nodes, adjacency) { EdgeCount = liveEdges.Count };
    }

    public bool Contains(string qualifiedName) => _nodes.ContainsKey(qualifiedName);

    public SchemaGraphNode? GetNode(string qualifiedName) =>
        _nodes.TryGetValue(qualifiedName, out var node) ? node : null;

    /// <summary>
    /// Shortest join path between two tables, or null when none exists within
    /// <paramref name="maxDepth"/> hops. Junction tables are traversed at zero cost, so a many-to-many
    /// reached through a link table ranks alongside a direct relationship.
    /// </summary>
    public SchemaJoinPath? FindPath(string fromQualifiedName, string toQualifiedName, int maxDepth = DefaultMaxDepth)
    {
        if (!_nodes.ContainsKey(fromQualifiedName) || !_nodes.ContainsKey(toQualifiedName))
        {
            return null;
        }

        if (string.Equals(fromQualifiedName, toQualifiedName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 0-1 BFS: entering a junction costs nothing, entering any other table costs one logical join.
        // The deque keeps the frontier ordered by cost without a priority queue.
        var best = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [fromQualifiedName] = 0 };
        var hops = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [fromQualifiedName] = 0 };
        var previous = new Dictionary<string, SchemaJoinStep>(StringComparer.OrdinalIgnoreCase);
        var queue = new LinkedList<string>();
        queue.AddFirst(fromQualifiedName);

        var visited = 0;
        while (queue.Count > 0)
        {
            visited++;
            if (visited > FrontierLimit)
            {
                break;
            }

            var current = queue.First!.Value;
            queue.RemoveFirst();

            if (string.Equals(current, toQualifiedName, StringComparison.OrdinalIgnoreCase))
            {
                return Reconstruct(fromQualifiedName, toQualifiedName, previous);
            }

            if (hops[current] >= maxDepth)
            {
                continue;
            }

            foreach (var step in _adjacency[current])
            {
                var weight = step.ToIsJunction ? 0 : 1;
                var candidateCost = best[current] + weight;
                var isImprovement = !best.TryGetValue(step.ToQualifiedName, out var knownCost) || candidateCost < knownCost;
                if (!isImprovement)
                {
                    continue;
                }

                best[step.ToQualifiedName] = candidateCost;
                hops[step.ToQualifiedName] = hops[current] + 1;
                previous[step.ToQualifiedName] = step;

                if (weight == 0)
                {
                    queue.AddFirst(step.ToQualifiedName);
                }
                else
                {
                    queue.AddLast(step.ToQualifiedName);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Expands seed tables into the set worth describing in full: the seeds, every table on a join path
    /// between two seeds, then direct neighbours until <paramref name="maxTables"/> is reached. Junction
    /// tables on a selected path bypass the cap — omitting one makes its path unusable, which is worse
    /// than describing one table fewer.
    /// </summary>
    public SchemaExpansion Expand(IEnumerable<string> seeds, int maxTables, int maxDepth = DefaultMaxDepth)
    {
        var seedList = seeds
            .Where(_nodes.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var selected = new HashSet<string>(seedList, StringComparer.OrdinalIgnoreCase);
        var paths = new List<SchemaJoinPath>();
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < seedList.Count; i++)
        {
            for (var j = i + 1; j < seedList.Count; j++)
            {
                var path = FindPath(seedList[i], seedList[j], maxDepth);
                if (path == null)
                {
                    continue;
                }

                paths.Add(path);
                foreach (var intermediate in path.IntermediateQualifiedNames)
                {
                    // A link table on a selected path is not optional — without it the join cannot run.
                    if (_nodes[intermediate].IsJunction)
                    {
                        required.Add(intermediate);
                        continue;
                    }

                    selected.Add(intermediate);
                }
            }
        }

        selected.UnionWith(required);

        var neighbours = new List<string>();
        foreach (var seed in seedList)
        {
            foreach (var step in _adjacency[seed])
            {
                if (!selected.Contains(step.ToQualifiedName))
                {
                    neighbours.Add(step.ToQualifiedName);
                }
            }
        }

        var candidateNeighbours = neighbours
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var remainingSlots = maxTables - selected.Count;
        var admitted = remainingSlots > 0 ? candidateNeighbours.Take(remainingSlots).ToList() : [];
        selected.UnionWith(admitted);

        var omitted = candidateNeighbours.Count - admitted.Count;

        return new SchemaExpansion(
            [.. selected],
            paths,
            Capped: omitted > 0,
            OmittedTableCount: omitted);
    }

    /// <summary>
    /// Connected components over the undirected relationship graph. Several components mean the schema is
    /// disconnected islands — a cross-island join cannot be grounded until someone declares the missing
    /// relationship.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> ConnectedComponents()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var components = new List<IReadOnlyList<string>>();

        foreach (var key in _nodes.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Contains(key))
            {
                continue;
            }

            var component = new List<string>();
            var queue = new Queue<string>();
            queue.Enqueue(key);
            seen.Add(key);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);

                foreach (var step in _adjacency[current])
                {
                    if (seen.Add(step.ToQualifiedName))
                    {
                        queue.Enqueue(step.ToQualifiedName);
                    }
                }
            }

            components.Add(component);
        }

        return components;
    }

    /// <summary>Tables with no relationship at all — where a user needs to declare one by hand.</summary>
    public IReadOnlyList<string> IsolatedTables() => _nodes.Keys
        .Where(x => _adjacency[x].Count == 0)
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public IReadOnlyList<string> JunctionTables() => _nodes.Values
        .Where(x => x.IsJunction)
        .Select(x => x.QualifiedName)
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// pgGraph's <c>classify_as_junction()</c>, widened to use registered relationships rather than only
    /// declared foreign keys — a warehouse link table has the same shape but no enforced constraints.
    /// </summary>
    private static bool IsJunction(TableMetadataDto table, IReadOnlyList<SchemaRelationshipEdge> edges)
    {
        var primaryKeyColumns = table.Columns
            .Where(x => x.IsPrimaryKey)
            .Select(x => x.ColumnName)
            .ToList();

        if (primaryKeyColumns.Count < 2)
        {
            return false;
        }

        var outgoing = edges
            .Where(x => x.SourceSchema.Equals(table.SchemaName, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.SourceTable.Equals(table.TableName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var referencingColumns = outgoing
            .Select(x => x.SourceColumn)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var everyKeyColumnReferences = primaryKeyColumns.All(referencingColumns.Contains);
        if (!everyKeyColumnReferences)
        {
            return false;
        }

        var distinctTargets = outgoing
            .Select(x => Qualify(x.TargetSchema, x.TargetTable))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return distinctTargets >= 2;
    }

    private static SchemaJoinPath Reconstruct(
        string fromQualifiedName,
        string toQualifiedName,
        IReadOnlyDictionary<string, SchemaJoinStep> previous)
    {
        var steps = new List<SchemaJoinStep>();
        var cursor = toQualifiedName;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The predecessor map is a tree rooted at the start node, so this terminates — but a bound costs
        // nothing and turns any future relaxation bug into a short path instead of a hung request.
        while (previous.TryGetValue(cursor, out var step) && seen.Add(cursor))
        {
            steps.Add(step);
            cursor = step.FromQualifiedName;

            if (string.Equals(cursor, fromQualifiedName, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        steps.Reverse();

        return new SchemaJoinPath(fromQualifiedName, toQualifiedName, steps);
    }

    private static bool ContainsTable(IReadOnlyList<TableMetadataDto> tables, string schemaName, string tableName) =>
        tables.Any(x => x.SchemaName.Equals(schemaName, StringComparison.OrdinalIgnoreCase)
            && x.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase));

    private static string Qualify(string schemaName, string tableName) => $"{schemaName}.{tableName}";
}
