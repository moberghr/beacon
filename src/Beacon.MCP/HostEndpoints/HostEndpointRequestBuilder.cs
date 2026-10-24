using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beacon.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Beacon.MCP.HostEndpoints;

/// <summary>The parts of one dispatched request, built from validated tool arguments.</summary>
internal sealed record HostEndpointRequestParts(
    string Path,
    QueryString QueryString,
    IReadOnlyDictionary<string, object?> RouteValues,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    IReadOnlyList<KeyValuePair<string, string>> FormFields,
    string? JsonBody)
{
    // The compiler-generated ToString prints every member; the argument-carrying ones (they may hold an Authorization
    // header or a password argument) are printed redacted.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Path = {Path}, QueryString = {RedactedPrint.Of(QueryString.Value)}, Headers = {(Headers is { Count: > 0 } ? RedactedPrint.Mask : string.Empty)}, FormFields = {(FormFields is { Count: > 0 } ? RedactedPrint.Mask : string.Empty)}, JsonBody = {RedactedPrint.Of(JsonBody)}");

        return true;
    }
}

internal sealed class HostEndpointArgumentException(string message) : Exception(message);

/// <summary>
/// Validates MCP arguments against a tool's parameters and turns them into a path (from the endpoint's route
/// pattern, URL-encoded), a query string, declared headers only, and a JSON or form body. Unknown and missing
/// required arguments are rejected before anything is dispatched, and so are route values routing could not have
/// delivered: a value that fails the route's constraints, a <c>/</c> in a single-segment value, or a <c>.</c> or
/// <c>..</c> segment. Caller-bound arguments
/// (<see cref="HostEndpointToolDescriptor.CallerBindings"/>) are not the agent's: they take
/// <paramref name="callerValues"/>, the values the host principal carries for them.
/// </summary>
internal static class HostEndpointRequestBuilder
{
    /// <param name="callerValues">The caller's value of each of the tool's caller-bound arguments, by name.</param>
    public static HostEndpointRequestParts Build(
        HostEndpointToolDescriptor tool,
        IDictionary<string, JsonElement>? arguments,
        IReadOnlyDictionary<string, string>? callerValues = null)
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

        var bound = tool.CallerBindings
            .Where(x => x.Parameter != null)
            .Select(x => (x.Parameter!, Value: JsonSerializer.SerializeToElement(CallerValue(callerValues, x.Name))));
        var asked = tool.Parameters
            .Where(x => supplied.ContainsKey(x.ArgumentName))
            .Select(x => (x, Value: supplied[x.ArgumentName]));

        foreach (var (parameter, value) in asked.Concat(bound))
        {
            if (parameter.Source == HostEndpointParameterSource.Body)
            {
                jsonBody = value.GetRawText();
                continue;
            }

            var pairs = Pairs(parameter, value);
            if (parameter.Shape == HostEndpointParameterShape.RawFields)
            {
                RefuseCallerBoundFields(tool, pairs.Select(x => x.Key));
            }

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

        jsonBody = WithCallerBoundProperties(tool, jsonBody, callerValues);

        RefuseUndeliverableRouteValues(tool.Endpoint.RoutePattern, routeArguments);

        var routeValues = BuildRouteValues(tool.Endpoint.RoutePattern, routeArguments);
        var path = BuildPath(tool.Endpoint.RoutePattern, routeValues);
        CheckRouteConstraints(tool, routeValues);

        return new HostEndpointRequestParts(path, BuildQueryString(query), routeValues, headers, form, jsonBody);
    }

    // A field the agent names after a caller-bound argument, in any casing, would compete with the caller's value: refused.
    private static void RefuseCallerBoundFields(HostEndpointToolDescriptor tool, IEnumerable<string> names)
    {
        var bound = tool.CallerBindings
            .Select(x => x.BodyProperty ?? x.Name)
            .Concat(tool.CallerBoundNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var supplied = names
            .Where(bound.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        if (supplied.Count > 0)
        {
            throw new HostEndpointArgumentException($"Invalid arguments: {string.Join(", ", supplied)} is filled from the caller and cannot be sent.");
        }
    }

    private static string CallerValue(IReadOnlyDictionary<string, string>? callerValues, string name) =>
        callerValues != null && callerValues.TryGetValue(name, out var value)
            ? value
            : throw new HostEndpointArgumentException($"Forbidden: the host application gives this caller no value for '{name}'.");

    // The caller-bound properties of the JSON body, written over whatever the agent sent for them, as their schema type.
    private static string? WithCallerBoundProperties(
        HostEndpointToolDescriptor tool,
        string? jsonBody,
        IReadOnlyDictionary<string, string>? callerValues)
    {
        var properties = tool.CallerBindings
            .Where(x => x.BodyProperty != null)
            .ToList();
        var agentBody = jsonBody == null ? null : JsonNode.Parse(jsonBody);

        // JSON binding matches property names ignoring case, so the agent's key in any casing would compete with the bound one.
        if (agentBody is JsonObject sent)
        {
            RefuseCallerBoundFields(tool, sent.Select(x => x.Key));
        }

        if (properties.Count == 0)
        {
            return jsonBody;
        }

        if ((agentBody ?? new JsonObject()) is not JsonObject body)
        {
            throw new HostEndpointArgumentException("Invalid arguments: the request body must be an object.");
        }

        foreach (var property in properties)
        {
            body[property.BodyProperty!] = TypedValue(property, CallerValue(callerValues, property.Name));
        }

        return body.ToJsonString();
    }

    private static JsonNode TypedValue(HostEndpointCallerBinding binding, string value)
    {
        switch (binding.BodyType)
        {
            case "integer" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer):
                return JsonValue.Create(integer);
            case "number" when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number):
                return JsonValue.Create(number);
            case "boolean" when bool.TryParse(value, out var flag):
                return JsonValue.Create(flag);
            case "integer" or "number" or "boolean":
                throw new HostEndpointArgumentException($"Forbidden: the caller's value for '{binding.Name}' is not a {binding.BodyType}.");
            default:
                return JsonValue.Create(value);
        }
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

    // Routing reads a single-segment value from one path segment, and the server drops '.' and '..' segments from the
    // path before routing, so neither reaches an endpoint on a real request.
    private static void RefuseUndeliverableRouteValues(RoutePattern pattern, IReadOnlyDictionary<string, string> routeArguments)
    {
        foreach (var (name, value) in routeArguments)
        {
            var parameter = pattern.GetParameter(name);
            if (parameter == null)
            {
                continue;
            }

            var segments = value.Split('/');
            if (!parameter.IsCatchAll && segments.Length > 1)
            {
                throw new HostEndpointArgumentException($"Invalid arguments: '{name}' must be a single path segment, without '/'.");
            }

            if (segments.Any(x => x is "." or ".."))
            {
                throw new HostEndpointArgumentException($"Invalid arguments: '{name}' must not contain a '.' or '..' path segment.");
            }

            // In a complex segment ({a}-{b}, {id}.{ext}) routing splits at the literal between the parts, so a value
            // holding that literal would reach the endpoint as a different combination than routing could deliver.
            if (!parameter.IsCatchAll && ComplexSegmentSeparators(pattern, name).Any(x => value.Contains(x, StringComparison.Ordinal)))
            {
                throw new HostEndpointArgumentException($"Invalid arguments: '{name}' must not contain the separator that follows or precedes it in {pattern.RawText}.");
            }
        }
    }

    private static IEnumerable<string> ComplexSegmentSeparators(RoutePattern pattern, string name)
    {
        foreach (var segment in pattern.PathSegments)
        {
            if (segment.Parts.Count < 2
                || !segment.Parts.Any(x => x is RoutePatternParameterPart parameter && string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var literal in segment.Parts.OfType<RoutePatternLiteralPart>())
            {
                if (literal.Content.Length > 0)
                {
                    yield return literal.Content;
                }
            }
        }
    }

    // What the matcher does before it selects the endpoint. A constraint that throws (one that needs the HttpContext
    // routing would pass) refuses the value: it was not shown to pass.
    private static void CheckRouteConstraints(HostEndpointToolDescriptor tool, IReadOnlyDictionary<string, object?> routeValues)
    {
        if (tool.RouteConstraints.Count == 0)
        {
            return;
        }

        var values = new RouteValueDictionary(routeValues);
        foreach (var constraint in tool.RouteConstraints)
        {
            bool matched;
            try
            {
                matched = constraint.Constraint.Match(null, null, constraint.Parameter, values, RouteDirection.IncomingRequest);
            }
            catch (Exception)
            {
                matched = false;
            }

            if (!matched)
            {
                throw new HostEndpointArgumentException($"Invalid arguments: '{constraint.Parameter}' does not satisfy the route constraint of {tool.RouteTemplate}.");
            }
        }
    }

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
