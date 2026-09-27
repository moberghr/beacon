using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Beacon.MCP.HostEndpoints;

/// <summary>The parts of one dispatched request, built from validated tool arguments.</summary>
internal sealed record HostEndpointRequestParts(
    string Path,
    QueryString QueryString,
    IReadOnlyDictionary<string, object?> RouteValues,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    IReadOnlyList<KeyValuePair<string, string>> FormFields,
    string? JsonBody);

internal sealed class HostEndpointArgumentException(string message) : Exception(message);

/// <summary>
/// Validates MCP arguments against a tool's parameters and turns them into a path (from the endpoint's route
/// pattern, URL-encoded), a query string, declared headers only, and a JSON or form body. Unknown and missing
/// required arguments are rejected before anything is dispatched.
/// </summary>
internal static class HostEndpointRequestBuilder
{
    public static HostEndpointRequestParts Build(HostEndpointToolDescriptor tool, IDictionary<string, JsonElement>? arguments)
    {
        var supplied = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments ?? new Dictionary<string, JsonElement>())
        {
            if (argument.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                supplied[argument.Key] = argument.Value;
            }
        }

        var known = tool.Parameters
            .Select(x => x.ArgumentName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = supplied.Keys
            .Where(x => !known.Contains(x))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
        {
            var allowed = known.Count == 0 ? "none" : string.Join(", ", known.OrderBy(x => x, StringComparer.Ordinal));
            throw new HostEndpointArgumentException($"Invalid arguments: unknown argument(s) {string.Join(", ", unknown)}. Allowed: {allowed}.");
        }

        var missing = tool.Parameters
            .Where(x => x.Required)
            .Where(x => !supplied.ContainsKey(x.ArgumentName))
            .Select(x => x.ArgumentName)
            .ToList();
        if (missing.Count > 0)
        {
            throw new HostEndpointArgumentException($"Invalid arguments: missing required argument(s) {string.Join(", ", missing)}.");
        }

        var routeArguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var query = new List<KeyValuePair<string, string>>();
        var headers = new List<KeyValuePair<string, string>>();
        var form = new List<KeyValuePair<string, string>>();
        string? jsonBody = null;

        foreach (var parameter in tool.Parameters)
        {
            if (!supplied.TryGetValue(parameter.ArgumentName, out var value))
            {
                continue;
            }

            if (parameter.Source == HostEndpointParameterSource.Body)
            {
                jsonBody = value.GetRawText();
                continue;
            }

            var pairs = Pairs(parameter, value);
            switch (parameter.Source)
            {
                case HostEndpointParameterSource.Route:
                    routeArguments[parameter.HttpName] = pairs.Count == 1
                        ? pairs[0].Value
                        : throw new HostEndpointArgumentException($"Invalid arguments: '{parameter.ArgumentName}' must be a single value.");
                    break;
                case HostEndpointParameterSource.Header:
                    headers.AddRange(pairs);
                    break;
                case HostEndpointParameterSource.Form:
                    form.AddRange(pairs);
                    break;
                default:
                    query.AddRange(pairs);
                    break;
            }
        }

        var routeValues = BuildRouteValues(tool.Endpoint.RoutePattern, routeArguments);
        var path = BuildPath(tool.Endpoint.RoutePattern, routeValues);

        return new HostEndpointRequestParts(path, BuildQueryString(query), routeValues, headers, form, jsonBody);
    }

    public static string EncodeForm(IEnumerable<KeyValuePair<string, string>> fields) =>
        string.Join("&", fields.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));

    private static List<KeyValuePair<string, string>> Pairs(HostEndpointToolParameter parameter, JsonElement value)
    {
        if (parameter.Shape == HostEndpointParameterShape.RawFields)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new HostEndpointArgumentException($"Invalid arguments: '{parameter.ArgumentName}' must be an object of field values.");
            }

            var fields = new List<KeyValuePair<string, string>>();
            foreach (var member in value.EnumerateObject())
            {
                foreach (var item in ScalarsOf(parameter.ArgumentName, member.Value))
                {
                    fields.Add(new KeyValuePair<string, string>(member.Name, item));
                }
            }

            return fields;
        }

        if (value.ValueKind == JsonValueKind.Array && parameter.Shape != HostEndpointParameterShape.Collection)
        {
            throw new HostEndpointArgumentException($"Invalid arguments: '{parameter.ArgumentName}' must be a single value.");
        }

        return ScalarsOf(parameter.ArgumentName, value)
            .Select(x => new KeyValuePair<string, string>(parameter.HttpName, x))
            .ToList();
    }

    private static IEnumerable<string> ScalarsOf(string argumentName, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Where(x => x.ValueKind != JsonValueKind.Null)
                .Select(x => Scalar(argumentName, x))
                .ToList();
        }

        return value.ValueKind == JsonValueKind.Null ? [] : [Scalar(argumentName, value)];
    }

    private static string Scalar(string argumentName, JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new HostEndpointArgumentException($"Invalid arguments: '{argumentName}' must hold plain values, not nested objects or arrays.")
        };

    private static Dictionary<string, object?> BuildRouteValues(RoutePattern pattern, IReadOnlyDictionary<string, string> routeArguments)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in pattern.Defaults)
        {
            values[value.Key] = value.Value;
        }

        // MVC's required values (controller, action, area) are what the router would have matched.
        foreach (var value in pattern.RequiredValues)
        {
            if (value.Value is string text)
            {
                values[value.Key] = text;
            }
        }

        foreach (var argument in routeArguments)
        {
            values[argument.Key] = argument.Value;
        }

        return values;
    }

    private static string BuildPath(RoutePattern pattern, IReadOnlyDictionary<string, object?> values)
    {
        var segments = new List<string>();
        foreach (var segment in pattern.PathSegments)
        {
            var text = new StringBuilder();
            var skipped = false;

            foreach (var part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        text.Append(literal.Content);
                        break;
                    case RoutePatternSeparatorPart separator:
                        text.Append(separator.Content);
                        break;
                    case RoutePatternParameterPart parameter:
                        var value = values.TryGetValue(parameter.Name, out var bound) ? Convert.ToString(bound, System.Globalization.CultureInfo.InvariantCulture) : null;
                        if (string.IsNullOrEmpty(value))
                        {
                            if (!parameter.IsOptional && !parameter.IsCatchAll)
                            {
                                throw new HostEndpointArgumentException($"Invalid arguments: missing route value '{parameter.Name}'.");
                            }

                            skipped = true;
                            break;
                        }

                        text.Append(parameter.IsCatchAll
                            ? string.Join("/", value.Split('/').Select(Uri.EscapeDataString))
                            : Uri.EscapeDataString(value));
                        break;
                }
            }

            if (!(skipped && text.Length == 0))
            {
                segments.Add(text.ToString());
            }
        }

        return "/" + string.Join("/", segments);
    }

    private static QueryString BuildQueryString(IReadOnlyList<KeyValuePair<string, string>> query)
    {
        if (query.Count == 0)
        {
            return QueryString.Empty;
        }

        return QueryString.Create(query.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)));
    }
}
