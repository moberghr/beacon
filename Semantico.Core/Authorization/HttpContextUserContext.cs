using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Semantico.Core.Authorization;

/// <summary>
/// Default implementation that reads from HttpContext.User claims.
/// </summary>
internal sealed class HttpContextUserContext : ISemanticoUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextUserContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string? UserId =>
        _httpContextAccessor.HttpContext?.User.FindFirst(SemanticoClaims.UserId)?.Value
        ?? _httpContextAccessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public string? UserName =>
        _httpContextAccessor.HttpContext?.User.FindFirst(SemanticoClaims.UserName)?.Value
        ?? _httpContextAccessor.HttpContext?.User.Identity?.Name;

    public string? Email =>
        _httpContextAccessor.HttpContext?.User.FindFirst(ClaimTypes.Email)?.Value;

    public IEnumerable<string> Claims =>
        _httpContextAccessor.HttpContext?.User.Claims
            .Select(c => $"{c.Type}:{c.Value}")
        ?? Enumerable.Empty<string>();

    public bool HasClaim(string claimType, string? claimValue = null)
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user == null) return false;

        return claimValue == null
            ? user.HasClaim(c => c.Type == claimType)
            : user.HasClaim(claimType, claimValue);
    }

    public IDictionary<string, object> Metadata { get; } = new Dictionary<string, object>();

    public bool IsAuthenticated =>
        _httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated ?? false;
}
