using System.Text.RegularExpressions;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.SavedQueries;

/// <summary>
/// Binds tool arguments into a reviewed step's SQL as real database parameters (§1.10): each placeholder is replaced
/// by a generated parameter name (<c>@p0</c>, <c>@p1</c> …) in one pass, and the value goes into the parameter
/// dictionary — values never reach the SQL text.
/// </summary>
public static class SavedQueryParameterBinder
{
    /// <param name="values">Converted argument values keyed by parameter name; every step parameter must have one.</param>
    public static (string Sql, Dictionary<string, object?> Parameters) Bind(
        string sql,
        IReadOnlyList<QueryStepParameterSnapshot> stepParameters,
        IReadOnlyDictionary<string, object?> values)
    {
        var parameters = new Dictionary<string, object?>();
        var nameByPlaceholder = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var parameter in stepParameters)
        {
            if (string.IsNullOrEmpty(parameter.Placeholder) || nameByPlaceholder.ContainsKey(parameter.Placeholder))
            {
                continue;
            }

            if (!values.TryGetValue(parameter.Name, out var value))
            {
                throw new InvalidOperationException($"Missing value for parameter '{parameter.Name}'.");
            }

            var parameterName = $"p{parameters.Count}";
            parameters.Add(parameterName, value);
            nameByPlaceholder.Add(parameter.Placeholder, parameterName);
        }

        if (nameByPlaceholder.Count == 0)
        {
            return (sql, parameters);
        }

        // Longest placeholder first so one that contains another (e.g. {from} inside {from_date}) matches whole.
        var pattern = string.Join("|", nameByPlaceholder.Keys
            .OrderByDescending(x => x.Length)
            .Select(Regex.Escape));
        var boundSql = Regex.Replace(sql, pattern, x => "@" + nameByPlaceholder[x.Value], RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        return (boundSql, parameters);
    }
}
