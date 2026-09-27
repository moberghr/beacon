using System.Text.Json;
using System.Text.RegularExpressions;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.SavedQueries;

/// <summary>One argument of a saved-query tool, merged across the version's steps by name.</summary>
public sealed record SavedQueryToolParameter(string Name, ParameterType Type, string? Description);

/// <summary>The executable shape of one query version: its steps, final query and tool parameters.</summary>
/// <param name="Issue">Why the version cannot be a tool, or null when it can.</param>
public sealed record SavedQueryToolShape(
    IReadOnlyList<QueryStepSnapshot> Steps,
    IReadOnlyList<SavedQueryToolParameter> Parameters,
    string? Issue);

/// <summary>Naming and shape rules shared by the admin handler, the query detail, the MCP tools and the brief.</summary>
public static partial class SavedQueryToolRules
{
    /// <summary>Every saved-query tool is listed as <c>q_&lt;McpToolName&gt;</c>.</summary>
    public const string ToolNamePrefix = "q_";

    /// <summary>
    /// Argument the tool reserves to pick the project when the caller reaches it through several projects; a query
    /// parameter with this name would be ambiguous, so such a version is not callable.
    /// </summary>
    public const string ProjectArgumentName = "project_id";

    public const int MaxDescriptionLength = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>True when <paramref name="name"/> is a valid <c>McpToolName</c>: <c>^[a-z][a-z0-9_]{2,40}$</c>.</summary>
    public static bool IsValidName(string? name) =>
        name != null && NamePattern().IsMatch(name);

    public static string ToolName(string mcpToolName) => ToolNamePrefix + mcpToolName;

    /// <summary>
    /// Reads the version's step snapshots and merges their parameters into the tool's arguments. A version is not
    /// callable when it has no steps, a parameter name is not a plain identifier or is reserved, a parameter has no
    /// placeholder, or two steps declare the same parameter with different types.
    /// </summary>
    public static SavedQueryToolShape Inspect(string stepsJson)
    {
        List<QueryStepSnapshot> steps;
        try
        {
            steps = JsonSerializer.Deserialize<List<QueryStepSnapshot>>(stepsJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return new SavedQueryToolShape([], [], "The approved version's steps could not be read.");
        }

        steps = steps
            .OrderBy(x => x.StepOrder)
            .ToList();

        if (steps.Count == 0)
        {
            return new SavedQueryToolShape(steps, [], "The approved version has no steps.");
        }

        var parameters = new List<SavedQueryToolParameter>();
        foreach (var parameter in steps.SelectMany(x => x.Parameters))
        {
            if (!ParameterNamePattern().IsMatch(parameter.Name ?? string.Empty))
            {
                return new SavedQueryToolShape(steps, [], $"Parameter '{parameter.Name}' is not a plain identifier (letters, digits, underscore).");
            }

            if (string.Equals(parameter.Name, ProjectArgumentName, StringComparison.OrdinalIgnoreCase))
            {
                return new SavedQueryToolShape(steps, [], $"Parameter name '{ProjectArgumentName}' is reserved for MCP tools.");
            }

            if (string.IsNullOrEmpty(parameter.Placeholder))
            {
                return new SavedQueryToolShape(steps, [], $"Parameter '{parameter.Name}' has no placeholder.");
            }

            var existing = parameters
                .Where(x => x.Name == parameter.Name)
                .FirstOrDefault();
            if (existing == null)
            {
                parameters.Add(new SavedQueryToolParameter(parameter.Name!, parameter.Type, parameter.Description));
                continue;
            }

            if (existing.Type != parameter.Type)
            {
                return new SavedQueryToolShape(steps, [], $"Parameter '{parameter.Name}' has different types in different steps.");
            }
        }

        return new SavedQueryToolShape(steps, parameters, null);
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{2,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterNamePattern();
}
