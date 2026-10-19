using System.Security.Claims;
using Beacon.Core.Authorization;
using Microsoft.AspNetCore.Http;

namespace Beacon.Shared.Http;

/// <summary>
/// The <see cref="IBeaconRequestPrincipal"/> of a web host: the current <c>HttpContext</c>, read on every call through
/// <see cref="IHttpContextAccessor"/> (itself a singleton over an async-local), so one instance serves every scope. A
/// scope without a current <c>HttpContext</c> (a background job) has no request. One of Beacon's own principals
/// (<see cref="IBeaconOwnRequestPrincipal"/>), so a host's explicit <c>UseRequestPrincipal</c> replaces it.
/// </summary>
internal sealed class HttpContextRequestPrincipal(IHttpContextAccessor httpContextAccessor) : IBeaconOwnRequestPrincipal
{
    public bool HasRequest => httpContextAccessor.HttpContext != null;

    public ClaimsPrincipal? User => httpContextAccessor.HttpContext?.User;
}
