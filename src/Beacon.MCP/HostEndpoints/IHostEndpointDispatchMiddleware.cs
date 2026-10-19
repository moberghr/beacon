using Beacon.Core.Mcp;
using Microsoft.AspNetCore.Http;

namespace Beacon.Core.HostEndpoints;

/// <summary>
/// The host's stand-in for its own middleware on a dispatched host endpoint tool call — host middleware does not run
/// for endpoint tools, only the endpoint's pipeline. Register zero or more; they are chained in registration order
/// with the endpoint's request delegate as the innermost <c>next</c>. The chain starts after <c>User</c>,
/// <c>RequestServices</c>, the endpoint and route values are set and after Beacon's authorization check.
/// </summary>
/// <remarks>
/// Ambient state set before <c>await next()</c> (<c>Thread.CurrentPrincipal</c>, an <c>AsyncLocal</c>) is visible to
/// the endpoint and its filters, exactly as in ASP.NET Core middleware. Not calling <c>next</c> refuses the call; an
/// exception fails it. Both return a generic tool error and are audited.
/// </remarks>
public interface IHostEndpointDispatchMiddleware
{
    Task InvokeAsync(HttpContext syntheticContext, McpCaller caller, Func<Task> next, CancellationToken cancellationToken);
}
