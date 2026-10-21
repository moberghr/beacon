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

/// <summary>
/// An argument filled from the caller (<c>HostEndpointToolOptions.BindFromCaller</c>) and left out of the tool's schema:
/// a scalar route/query/header/form <see cref="Parameter"/>, or the top-level <see cref="BodyProperty"/> of the JSON body,
/// whose value is written as <see cref="BodyType"/> (the property's JSON schema type).
/// </summary>
internal sealed record HostEndpointCallerBinding(
    string Name,
    HostEndpointToolParameter? Parameter,
    string? BodyProperty = null,
    string? BodyType = null);

/// <param name="ReadOnly">The declared read-only flag; false for a write tool (only with <c>AllowWriteTools</c>).</param>
/// <param name="Destructive">Write tools only: the declared destructive flag.</param>
/// <param name="Idempotent">Write tools only: the declared idempotent flag.</param>
internal sealed record HostEndpointToolDescriptor(
    string Name,
    string ToolName,
    string Description,
    string HttpMethod,
    RouteEndpoint Endpoint,
    IReadOnlyList<HostEndpointToolParameter> Parameters,
    JsonElement InputSchema,
    bool ReadOnly,
    bool Destructive,
    bool Idempotent)
{
    public const string ToolNamePrefix = "api_";

    /// <summary>The arguments the call carries from the caller rather than from the agent; not in <see cref="Parameters"/>.</summary>
    public IReadOnlyList<HostEndpointCallerBinding> CallerBindings { get; init; } = [];

    /// <summary>Appended to a write tool's name in its audit row, so write calls are distinguishable (and filterable).</summary>
    public const string WriteAuditSuffix = ":write";

    public string RouteTemplate => "/" + (Endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');

    /// <summary>The <c>McpAuditLog.Tool</c> value: <c>api_&lt;name&gt;</c>, or <c>api_&lt;name&gt;:write</c> for a write tool.</summary>
    public string AuditToolName => ReadOnly ? ToolName : ToolName + WriteAuditSuffix;

    public Tool ToProtocolTool() =>
        new()
        {
            Name = ToolName,
            Title = Name,
            Description = Description,
            InputSchema = InputSchema,
            Annotations = ToAnnotations()
        };

    /// <summary>
    /// The MCP hints come from the declaration, never from the HTTP verb. A write tool's <c>readOnlyHint = false</c> is
    /// what makes clients ask the user before calling it.
    /// </summary>
    public ToolAnnotations ToAnnotations() =>
        ReadOnly
            ? new ToolAnnotations
            {
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false
            }
            : new ToolAnnotations
            {
                ReadOnlyHint = false,
                DestructiveHint = Destructive,
                IdempotentHint = Idempotent,
                OpenWorldHint = false
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
