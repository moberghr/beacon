using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ModelContextProtocol.Protocol;

namespace Beacon.MCP.HostEndpoints;

internal enum HostEndpointParameterSource
{
    Route,
    Query,
    Header,
    Form,
    Body
}

/// <summary>How an argument's value is sent: one scalar, repeated keys, the raw JSON body, or raw fields.</summary>
internal enum HostEndpointParameterShape
{
    Scalar,
    Collection,
    Json,

    /// <summary>An object whose members are sent as individual fields (a parameter with a custom model binder).</summary>
    RawFields
}

/// <summary>One MCP argument and where it goes on the dispatched request.</summary>
/// <param name="ArgumentName">The MCP argument name.</param>
/// <param name="HttpName">The route value / query key / header / form field name.</param>
internal sealed record HostEndpointToolParameter(
    string ArgumentName,
    string HttpName,
    HostEndpointParameterSource Source,
    HostEndpointParameterShape Shape,
    bool Required,
    string? Description,
    JsonElement Schema);

internal sealed record HostEndpointToolDescriptor(
    string Name,
    string ToolName,
    string Description,
    string HttpMethod,
    RouteEndpoint Endpoint,
    IReadOnlyList<HostEndpointToolParameter> Parameters,
    JsonElement InputSchema)
{
    public const string ToolNamePrefix = "api_";

    public string RouteTemplate => "/" + (Endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');

    public Tool ToProtocolTool() =>
        new()
        {
            Name = ToolName,
            Title = Name,
            Description = Description,
            InputSchema = InputSchema,
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false
            }
        };

    public string FirstDescriptionLine()
    {
        var line = Description.Split('\n', 2)[0].Trim();

        return line.Length > 200 ? line[..200] : line;
    }

    public static bool HasBody(string httpMethod) =>
        !HttpMethods.IsGet(httpMethod)
        && !HttpMethods.IsHead(httpMethod)
        && !HttpMethods.IsDelete(httpMethod)
        && !HttpMethods.IsOptions(httpMethod)
        && !HttpMethods.IsTrace(httpMethod);
}
