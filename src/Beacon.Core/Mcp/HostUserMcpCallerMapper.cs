using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authentication;
using Beacon.Core.HostData;

namespace Beacon.Core.Mcp;

/// <summary>
/// An <see cref="IMcpCallerMapper"/> for a host whose own users decide who may use MCP (the MCP gateway): a validated
/// bearer token is a caller when it is a delegated Entra access token of <see cref="HostUserMcpCallerOptions.TenantId"/>,
/// for one of the audiences Beacon validates, carrying <see cref="HostUserMcpCallerOptions.RequiredScope"/>, from an
/// allowed client when <see cref="HostUserMcpCallerOptions.AllowedClientIds"/> is set, and the host's
/// <see cref="IMcpHostUserResolver"/> knows its person. Such a caller reaches the host project only, with the Execute
/// scope; what it may do inside the host is the host's decision per tool call (<see cref="HostUserMcpHostPrincipalFactory"/>).
/// </summary>
/// <remarks>
/// Refused: other tenants and audiences, ID tokens and app-only tokens (no system callers), tokens without <c>oid</c> or
/// the scope, other clients, people the host does not know, and everyone while the host project has not been synced
/// (<see cref="IBeaconHostProjectGate"/>). Entra roles and groups are never read. Refusals are logged with the caller
/// hash only. No Beacon user is provisioned.
/// </remarks>
public sealed partial class HostUserMcpCallerMapper(
    IMcpHostUserResolver users,
    IBeaconHostProjectGate hostProject,
    McpCallerSubjectHasher subjectHasher,
    IOptions<HostUserMcpCallerOptions> options,
    ILogger<HostUserMcpCallerMapper> logger,
    JwtAuthenticationOptions? jwtOptions = null) : IMcpCallerMapper
{
    // Carried on the caller to the principal factory, which resolves the user again for every tool call. Never on a
    // principal, so a token cannot set them.
    internal const string TenantClaim = "beacon:mcp_host_user:tid";
    internal const string ObjectIdClaim = "beacon:mcp_host_user:oid";
    internal const string ClientIdClaim = "beacon:mcp_host_user:azp";
    internal const string ScopesClaim = "beacon:mcp_host_user:scp";

    public async Task<McpCaller?> MapAsync(ClaimsPrincipal jwtPrincipal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jwtPrincipal);

        var settings = options.Value;
        var tenantId = Claim(jwtPrincipal, "tid");
        if (tenantId == null || !string.Equals(tenantId, settings.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            LogRefused(logger, null, "the token is not from the accepted tenant");
            return null;
        }

        // Validated by the bearer middleware already; re-checked so the mapper fails closed on its own.
        var audiences = jwtOptions?.Validation.EffectiveAudiences() ?? [];
        if (!jwtPrincipal.FindAll("aud").Any(x => audiences.Contains(x.Value, StringComparer.Ordinal)))
        {
            LogRefused(logger, null, "the token is not for one of the audiences Beacon validates");
            return null;
        }

        var objectId = Claim(jwtPrincipal, "oid");
        if (objectId == null)
        {
            LogRefused(logger, null, "the token has no oid");
            return null;
        }

        var callerHash = subjectHasher.Hash(tenantId, objectId);
        if (ConfiguredMcpCallerMapper.ClassifyToken(jwtPrincipal) != McpTokenKind.Delegated)
        {
            LogRefused(logger, callerHash, "not a delegated user access token");
            return null;
        }

        var scopes = Scopes(jwtPrincipal);
        if (!scopes.Contains(settings.RequiredScope, StringComparer.Ordinal))
        {
            LogRefused(logger, callerHash, "the token does not carry the MCP scope");
            return null;
        }

        var clientId = Claim(jwtPrincipal, "azp") ?? Claim(jwtPrincipal, "appid");
        if (settings.AllowedClientIds.Count > 0 && !settings.AllowedClientIds.Contains(clientId ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            LogRefused(logger, callerHash, "the token was issued to a client application that is not allowed");
            return null;
        }

        var user = await users.FindAsync(new McpTokenIdentity(tenantId, objectId, clientId, scopes), cancellationToken);
        if (user == null)
        {
            LogRefused(logger, callerHash, "the host application has no user for this token");
            return null;
        }

        var projectId = await hostProject.GetProjectIdAsync(cancellationToken);
        if (projectId == null)
        {
            LogRefused(logger, callerHash, "the host project has not been synced yet");
            return null;
        }

        List<Claim> carried =
        [
            new(TenantClaim, tenantId),
            new(ObjectIdClaim, objectId),
            new(ScopesClaim, string.Join(' ', scopes))
        ];
        if (clientId != null)
        {
            carried.Add(new Claim(ClientIdClaim, clientId));
        }

        return new McpCaller(
            McpCallerKind.User,
            user.DisplayName,
            callerHash,
            BeaconUserId: null,
            AllowedProjectIds: [projectId.Value],
            McpCallerScope.Execute,
            HostClaims: carried);
    }

    /// <summary>The token identity the mapper carried on <paramref name="caller"/>, or <c>null</c> when it carries none.</summary>
    internal static McpTokenIdentity? TokenIdentityOf(McpCaller caller)
    {
        string? Carried(string type) => caller.HostClaims
            .Where(x => string.Equals(x.Type, type, StringComparison.Ordinal))
            .Select(x => x.Value)
            .FirstOrDefault();

        var tenantId = Carried(TenantClaim);
        var objectId = Carried(ObjectIdClaim);
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(objectId))
        {
            return null;
        }

        var scopes = (Carried(ScopesClaim) ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return new McpTokenIdentity(tenantId, objectId, Carried(ClientIdClaim), scopes);
    }

    // scp is one space-separated claim, or several from some handlers.
    private static List<string> Scopes(ClaimsPrincipal principal) =>
        principal.FindAll("scp")
            .SelectMany(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string? Claim(ClaimsPrincipal principal, string type)
    {
        var value = principal.FindFirst(type)?.Value;

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP caller {CallerHash} refused: {Reason}.")]
    private static partial void LogRefused(ILogger logger, string? callerHash, string reason);
}

/// <summary>
/// The principal a host endpoint tool call runs as for a <see cref="HostUserMcpCallerMapper"/> caller: the host user,
/// resolved again through <see cref="IMcpHostUserResolver"/> on every call (a deactivated user is refused at once), with
/// the claims the host gave it, on an identity of type <see cref="DefaultMcpHostPrincipalFactory.UserAuthenticationType"/>
/// (<see cref="McpHostPrincipalExtensions.IsBeaconMcpCall"/>). A system caller, or one the mapper did not map, is refused.
/// </summary>
public sealed class HostUserMcpHostPrincipalFactory(IServiceScopeFactory scopes) : IMcpHostPrincipalFactory
{
    public async Task<ClaimsPrincipal?> CreateAsync(McpCaller caller, ClaimsPrincipal mcpPrincipal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (caller.Kind != McpCallerKind.User || HostUserMcpCallerMapper.TokenIdentityOf(caller) is not { } token)
        {
            return null;
        }

        // The resolver reads the host's store, which is scoped; the factory is a singleton.
        await using var scope = scopes.CreateAsyncScope();
        var user = await scope.ServiceProvider
            .GetRequiredService<IMcpHostUserResolver>()
            .FindAsync(token, cancellationToken);
        if (user == null)
        {
            return null;
        }

        var claims = user.Claims
            .Select(x => new Claim(x.Type, x.Value))
            .ToList();
        if (!claims.Any(x => x.Type == ClaimTypes.Name))
        {
            claims.Add(new Claim(ClaimTypes.Name, user.DisplayName));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            DefaultMcpHostPrincipalFactory.UserAuthenticationType,
            ClaimTypes.Name,
            ClaimTypes.Role));
    }
}
