using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.Services;

/// <summary>
/// A query's executable content, and its SHA-256: the steps in run order (by step order, then as listed — the stored
/// steps by id) with their names, data sources, SQL and parameters (name, type, placeholder), and the final query.
/// Descriptions, the query's own name and the data sources' display names are left out — they change nothing a run
/// executes — so the same content hashes alike whether it is read from the stored steps, an edit or a version's snapshot.
/// A missing final query and an empty one are the same content, as they are to a run; so are a missing step name and an
/// empty one. Line endings in the SQL and the final query are normalised (CRLF and CR read as LF); any other whitespace
/// is content. The approval workflow decides with it whether an edit changes what runs (<see cref="SameAs"/>), stores it
/// on every version (<see cref="QueryVersion.ContentHash"/>) and on an approval, and compares a run's stored steps with
/// the reviewed version.
/// </summary>
internal static class QueryContentHash
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static QueryContent Of(Query query)
    {
        return Of(
            query.FinalQuery,
            query.Steps
                .OrderBy(x => x.StepOrder)
                .ThenBy(x => x.Id)
                .Select(x =>
                    new ContentStep(
                        x.StepOrder,
                        x.DataSourceId,
                        x.Name,
                        x.SqlValue,
                        x.Parameters
                            .Select(y => new ContentParameter(y.Name, y.Type, y.Placeholder))
                            .ToList())));
    }

    public static QueryContent Of(string? finalQuery, IEnumerable<QueryStepData> steps)
    {
        return Of(
            finalQuery,
            steps.Select(x =>
                new ContentStep(
                    x.StepOrder,
                    x.DataSourceId,
                    x.Name,
                    x.SqlValue,
                    x.Parameters
                        .Select(y => new ContentParameter(y.Name, y.Type, y.Placeholder))
                        .ToList())));
    }

    public static QueryContent Of(string? finalQuery, IEnumerable<QueryStepSnapshot> steps)
    {
        return Of(
            finalQuery,
            steps.Select(x =>
                new ContentStep(
                    x.StepOrder,
                    x.DataSourceId,
                    x.Name,
                    x.SqlValue,
                    x.Parameters
                        .Select(y => new ContentParameter(y.Name, y.Type, y.Placeholder))
                        .ToList())));
    }

    /// <summary>
    /// The content of a version: its stored hash when it has one, else computed from its snapshot. Null when the snapshot
    /// cannot be read, which no comparison accepts.
    /// </summary>
    public static string? OfVersion(string? storedHash, string stepsJson, string? finalQuery)
    {
        if (storedHash != null)
        {
            return storedHash;
        }

        return OfSnapshot(stepsJson, finalQuery)?.Hash;
    }

    /// <summary>The content a version's snapshot holds, or null when <paramref name="stepsJson"/> cannot be read.</summary>
    public static QueryContent? OfSnapshot(string stepsJson, string? finalQuery)
    {
        try
        {
            var steps = JsonSerializer.Deserialize<List<QueryStepSnapshot>>(stepsJson, JsonOptions);

            return steps == null ? null : Of(finalQuery, steps);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The steps in run order: by step order, then as given (OrderBy is stable), so steps that share an order keep the
    // order they run in and a swap between them is a change.
    private static QueryContent Of(string? finalQuery, IEnumerable<ContentStep> steps)
    {
        var canonical = new CanonicalContent(
            NormalisedLineEndings(finalQuery),
            steps
                .Select(x =>
                    new CanonicalStep(
                        x.Order,
                        x.DataSourceId,
                        x.Name ?? string.Empty,
                        NormalisedLineEndings(x.Sql),
                        x.Parameters
                            .Select(y => new CanonicalParameter(y.Name ?? string.Empty, (int)y.Type, y.Placeholder ?? string.Empty))
                            .OrderBy(y => y.Name, StringComparer.Ordinal)
                            .ThenBy(y => y.Placeholder, StringComparer.Ordinal)
                            .ThenBy(y => y.Type)
                            .ToList()))
                .OrderBy(x => x.Order)
                .ToList());

        var json = JsonSerializer.Serialize(canonical, JsonOptions);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

        return new QueryContent(hash, canonical.Steps.Count == 0 && canonical.FinalQuery.Length == 0);
    }

    // The same SQL saved from Windows and from anywhere else is the same content; every other character counts.
    private static string NormalisedLineEndings(string? sql)
    {
        return (sql ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private sealed record ContentStep(int Order, int DataSourceId, string? Name, string? Sql, IReadOnlyList<ContentParameter> Parameters);

    private sealed record ContentParameter(string? Name, ParameterType Type, string? Placeholder);

    // Serialized in declaration order: the hash depends on it.
    private sealed record CanonicalContent(string FinalQuery, IReadOnlyList<CanonicalStep> Steps);

    private sealed record CanonicalStep(int Order, int DataSourceId, string Name, string Sql, IReadOnlyList<CanonicalParameter> Parameters);

    private sealed record CanonicalParameter(string Name, int Type, string Placeholder);
}

/// <param name="Hash">SHA-256 of the canonical content, lowercase hex (64 characters).</param>
/// <param name="IsEmpty">No steps and no final query: nothing would run.</param>
internal sealed record QueryContent(string Hash, bool IsEmpty)
{
    /// <summary>True when <paramref name="other"/> is the same executable content.</summary>
    public bool SameAs(QueryContent other)
    {
        return string.Equals(Hash, other.Hash, StringComparison.Ordinal);
    }
}
