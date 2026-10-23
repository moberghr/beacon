using System.Reflection;
using System.Security.Claims;
using Beacon.Api.Authentication;
using Beacon.Api.Hubs;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Beacon.Api.Endpoints;

internal static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/auth/me", GetCurrentUser)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithName("GetCurrentUser")
            .WithTags("Auth");

        // Anonymous: the login page reads this pre-auth to decide whether to offer SSO and the password form.
        group.MapGet("/auth/sso", GetSsoConfig)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithName("GetSsoConfig")
            .WithTags("Auth");

        // Authenticated but not permission-gated: a user without a role gets { canRead: false } (the shell shows a
        // "no access yet" message) rather than a 403. Also names the functional permissions the caller holds now, for the
        // shell to show the pages they open, and the running build, which the signed-in shell shows (never before sign-in).
        group.MapGet("/auth/permissions", GetCurrentPermissions)
            .SkipBeaconPermissionCheck()
            .WithName("GetCurrentPermissions")
            .WithTags("Auth");

        return group;
    }

    // LocalLoginEnabled is Beacon:UserManagement:LocalLogin:Enabled: when false the login page shows no password form (a
    // break-glass account opens it with ?local=1). Without Beacon's configuration the password form is not offered.
    internal static SsoConfigResponse GetSsoConfig(IOptions<OidcAuthenticationOptions> oidcOptions, HttpContext httpContext)
    {
        var configuration = httpContext.RequestServices.GetService<BeaconConfiguration>();

        return new SsoConfigResponse(oidcOptions.Value.Enabled, configuration?.UserManagement.LocalLogin.Enabled ?? false);
    }

    // RealtimeEnabled is per caller once signed in: true only where the hub would admit them (BeaconHubAccess), so the
    // shell never opens a connection the hub refuses. SignOutEverywhereEnabled: the caller is a signed-in session (no API
    // key, no MCP caller) on a host whose sessions are checked against Beacon's user store (user management on), so
    // "sign out everywhere" can end their other sessions. GatewayMode is reported signed out too, so the shell never shows
    // a signed-out visitor of the MCP gateway a page the gateway does not serve (first-run setup).
    internal static async Task<CurrentUserResponse> GetCurrentUser(
        IBeaconUserContext userContext,
        HttpContext httpContext,
        BeaconApiOptions? apiOptions)
    {
        var realtime = apiOptions?.Realtime ?? false;
        var externalLogin = ExternalLoginResponse.From(apiOptions?.ExternalLogin);
        var gatewayMode = apiOptions?.GatewayMode ?? false;

        if (!userContext.IsAuthenticated)
        {
            return CurrentUserResponse.Anonymous(realtime, externalLogin, gatewayMode);
        }

        var roles = httpContext.User
            .FindAll(ClaimTypes.Role)
            .Select(x => x.Value)
            .Distinct()
            .ToArray();

        return new CurrentUserResponse(
            UserId: userContext.UserId,
            UserName: userContext.UserName,
            DisplayName: userContext.DisplayName,
            Email: userContext.Email,
            IsAuthenticated: true,
            Roles: roles,
            RealtimeEnabled: realtime && await BeaconHubAccess.AllowsAsync(httpContext),
            ExternalLogin: externalLogin,
            SignOutEverywhereEnabled: SignOutEverywhereEnabled(httpContext),
            GatewayMode: gatewayMode);
    }

    private static bool SignOutEverywhereEnabled(HttpContext httpContext)
    {
        var userManagement = httpContext.RequestServices?.GetService<BeaconConfiguration>()?.UserManagement.Enabled == true;

        return userManagement
            && !BeaconUserLookup.IsApiKey(httpContext.User)
            && !BeaconUserLookup.IsMcpCaller(httpContext.User)
            && !string.IsNullOrEmpty(httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
    }

    private static async Task<CurrentPermissionsResponse> GetCurrentPermissions(
        IBeaconAuthorizationProvider authorization,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var migrationsEnabled = httpContext.RequestServices.GetService<IOptions<MigrationOptions>>()?.Value.Enabled == true;
        var canRead = await authorization.HasReadPermissionAsync(cancellationToken);
        var canWrite = await authorization.HasWritePermissionAsync(cancellationToken);
        var accessor = httpContext.RequestServices.GetService<IBeaconPermissionAccessor>();
        var permissions = accessor == null
            ? BeaconPermissionSet.None
            : await accessor.GetCurrentAsync(cancellationToken);

        return new CurrentPermissionsResponse(
            canRead,
            canWrite,
            permissions.IsAdmin,
            BeaconPermissions.All
                .Where(permissions.Holds)
                .ToArray(),
            migrationsEnabled,
            httpContext.RequestServices.GetService<BeaconApiOptions>()?.GatewayMode == true,
            BuildInfoResponse.Current);
    }
}

internal sealed record CurrentUserResponse(
    string? UserId,
    string? UserName,
    string? DisplayName,
    string? Email,
    bool IsAuthenticated,
    IReadOnlyList<string> Roles,
    bool RealtimeEnabled,
    ExternalLoginResponse? ExternalLogin,
    bool SignOutEverywhereEnabled = false,
    bool GatewayMode = false)
{
    /// <summary>
    /// The unauthenticated shape. Still carries <paramref name="realtimeEnabled"/> so the shell
    /// knows whether to open a hub connection once the user signs in, <paramref name="externalLogin"/>
    /// so it knows where to send the user to sign in, and <paramref name="gatewayMode"/> so it knows which pages the
    /// deployment serves — a static singleton cannot, because all three are host configuration rather than constants.
    /// </summary>
    public static CurrentUserResponse Anonymous(bool realtimeEnabled, ExternalLoginResponse? externalLogin, bool gatewayMode) =>
        new(null, null, null, null, false, Array.Empty<string>(), realtimeEnabled, externalLogin, GatewayMode: gatewayMode);
}

internal sealed record ExternalLoginResponse(string LoginUrl, string ReturnUrlParameter)
{
    public static ExternalLoginResponse? From(ExternalLoginOptions? options) =>
        options == null ? null : new ExternalLoginResponse(options.LoginUrl, options.ReturnUrlParameter);
}

/// <summary>
/// What the caller may do now: read and write (its global role), whether it is an Admin, the functional permissions it
/// holds (<see cref="BeaconPermissions"/>; an Admin holds every one), and whether this deployment runs data migrations
/// (<c>Beacon:Migrations:Enabled</c>; the shell hides the feature when it does not), and the running build
/// (<see cref="BuildInfoResponse"/>), which is served to signed-in callers only.
/// </summary>
internal sealed record CurrentPermissionsResponse(
    bool CanRead,
    bool CanWrite,
    bool IsAdmin,
    IReadOnlyList<string> Permissions,
    bool MigrationsEnabled,
    bool GatewayMode = false,
    BuildInfoResponse? Build = null);

/// <summary>
/// The running build: the Beacon.Api assembly's informational version split into the release version and the source
/// commit the SDK appends after <c>+</c> (<see langword="null"/> when the build recorded none).
/// </summary>
internal sealed record BuildInfoResponse(string Version, string? Commit)
{
    public static readonly BuildInfoResponse Current = From(
        typeof(BuildInfoResponse).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        typeof(BuildInfoResponse).Assembly.GetName().Version);

    internal static BuildInfoResponse From(string? informationalVersion, Version? assemblyVersion)
    {
        var parts = (informationalVersion ?? string.Empty).Split('+', 2);
        var version = parts[0].Trim();
        var commit = parts.Length == 2 ? parts[1].Trim() : string.Empty;

        return new BuildInfoResponse(
            version.Length > 0 ? version : assemblyVersion?.ToString(3) ?? "0.0.0",
            commit.Length > 0 ? commit : null);
    }
}

/// <summary>Whether single sign-on is offered (<c>Enabled</c>), and whether password sign-in is open to everyone.</summary>
internal sealed record SsoConfigResponse(bool Enabled, bool LocalLoginEnabled);
