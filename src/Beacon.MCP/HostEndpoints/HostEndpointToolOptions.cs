using System.Security.Claims;

namespace Beacon.MCP.HostEndpoints;

/// <summary>Options for <see cref="HostEndpointToolsServiceCollectionExtensions.AddHostEndpointTools"/>.</summary>
public sealed class HostEndpointToolOptions
{
    /// <summary>
    /// The Beacon project the tools belong to. Only callers authorized for this project see or call them; when the
    /// project does not exist yet, no tool is listed (fail closed). Required.
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>The largest response body Beacon accepts from an endpoint; past it the call fails. Default 1 MiB.</summary>
    public int MaxResponseBytes { get; set; } = 1_048_576;

    /// <summary>
    /// Up to this many tools, each is its own MCP tool <c>api_&lt;name&gt;</c>. Above it, the tools are reached through
    /// <c>search_api</c> and <c>call_api</c> so a large host does not flood the agent's tool list. Default 20.
    /// </summary>
    public int NamedToolLimit { get; set; } = 20;

    /// <summary>How long one dispatched request may run. Default 30 seconds.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Authentication schemes an exposed endpoint's authorization policy may name. Beacon dispatches in-process as the
    /// principal <c>IMcpHostPrincipalFactory</c> builds for the MCP caller and never runs the named scheme's handler,
    /// so a policy that demands a specific scheme (a step-up or MFA scheme, a partner API scheme) would be satisfied
    /// without that scheme ever authenticating. An exposed endpoint whose combined policy names any scheme not listed
    /// here fails startup. List a scheme only when the MCP caller's principal is an acceptable stand-in for it.
    /// Default empty (matching is case-sensitive, as ASP.NET Core scheme names are).
    /// </summary>
    public List<string> TrustedAuthenticationSchemes { get; set; } = [];

    /// <summary>
    /// Expose endpoints declared <c>ReadOnly = false</c> as write tools. Off by default: such an endpoint then fails
    /// startup. When on, a write tool carries <c>readOnlyHint = false</c> (so MCP clients ask the user before calling
    /// it), runs only for a mapped Entra <b>user</b> caller (a system caller is refused and never sees it), and is
    /// audited as <c>api_&lt;name&gt;:write</c>. The endpoint keeps its own validation and authorization; Beacon adds
    /// none. Default false.
    /// </summary>
    public bool AllowWriteTools { get; set; }

    /// <summary>
    /// Run the host's <c>IExceptionHandler</c>s around a dispatched call, as <c>UseExceptionHandler()</c> runs them around
    /// a real request — host middleware does not run for endpoint tools. An endpoint's deliberate refusal (an exception
    /// the host maps to a 403, 404 or 400 problem) then reaches the agent as that status and body instead of "failed with
    /// an unexpected error". An exception no handler takes still fails the call generically. Default true.
    /// </summary>
    public bool UseHostExceptionHandlers { get; set; } = true;

    /// <summary>
    /// The note each tool's description ends with, after the endpoint's summary. Unset, it is Beacon's
    /// (<see cref="HostEndpointToolNote.DefaultNote"/>): the method and route, run with the caller's host permissions, and
    /// for a write tool that it changes data and is to be called only when the user asked. Set it when the endpoints run
    /// with less than the caller's own permissions (a self-service API, say) or the host words it otherwise; returning
    /// null or an empty string ends the description at the summary. Default null.
    /// </summary>
    public Func<HostEndpointToolNote, string?>? DescriptionNote { get; set; }

    private readonly Dictionary<string, Func<ClaimsPrincipal, string?>> _callerBindings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The arguments filled from the caller (<see cref="BindFromCaller(string, string)"/>), by name.</summary>
    internal IReadOnlyDictionary<string, Func<ClaimsPrincipal, string?>> CallerBindings => _callerBindings;

    /// <summary>
    /// Fills the endpoint argument named <paramref name="argument"/> (compared ignoring case) from the claim
    /// <paramref name="claimType"/> of the host principal the call runs as, instead of asking the agent for it. See
    /// <see cref="BindFromCaller(string, Func{ClaimsPrincipal, string})"/>.
    /// </summary>
    public HostEndpointToolOptions BindFromCaller(string argument, string claimType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);

        return BindFromCaller(argument, x => x.FindFirst(claimType)?.Value);
    }

    /// <summary>
    /// Fills the endpoint argument named <paramref name="argument"/> (compared ignoring case) from the host principal the
    /// call runs as — the person's own id in the host, which an agent cannot know. On every tool that binds such a route,
    /// query, header or form value, or a top-level property of its JSON body, the argument is left out of the tool's
    /// input schema, and the call carries the caller's value. An agent that sends a bound name anyway — as an argument, a
    /// raw field or a body property in any casing — is refused on every tool. A principal without a value refuses the call,
    /// and a name no tool takes is warned at startup.
    /// </summary>
    public HostEndpointToolOptions BindFromCaller(string argument, Func<ClaimsPrincipal, string?> value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument);
        ArgumentNullException.ThrowIfNull(value);

        if (!_callerBindings.TryAdd(argument.Trim(), value))
        {
            throw new InvalidOperationException($"AddHostEndpointTools: '{argument}' is already bound from the caller.");
        }

        return this;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProjectName))
        {
            throw new InvalidOperationException("AddHostEndpointTools: ProjectName is required.");
        }

        if (MaxResponseBytes <= 0)
        {
            throw new InvalidOperationException("AddHostEndpointTools: MaxResponseBytes must be positive.");
        }

        if (NamedToolLimit < 0)
        {
            throw new InvalidOperationException("AddHostEndpointTools: NamedToolLimit must not be negative.");
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("AddHostEndpointTools: RequestTimeout must be positive.");
        }
    }
}

/// <summary>One exposed endpoint, as <see cref="HostEndpointToolOptions.DescriptionNote"/> sees it.</summary>
/// <param name="Name">The declared tool name, without the <c>api_</c> prefix.</param>
/// <param name="HttpMethod">The HTTP method the tool calls.</param>
/// <param name="Route">The endpoint's route pattern, starting with <c>/</c>.</param>
/// <param name="ReadOnly">False for a write tool.</param>
public sealed record HostEndpointToolNote(string Name, string HttpMethod, string Route, bool ReadOnly)
{
    /// <summary>Beacon's note, used unless <see cref="HostEndpointToolOptions.DescriptionNote"/> is set.</summary>
    public string DefaultNote => ReadOnly
        ? $"Read-only host endpoint {HttpMethod} {Route}, run with your host permissions."
        : $"Write host endpoint {HttpMethod} {Route}: it changes data in the host application, as you, with your host permissions. Call it only when the user asked for this change.";
}
