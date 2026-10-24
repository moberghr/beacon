using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Beacon.UI;

/// <summary>
/// Hosts the Beacon React SPA from this assembly's static web assets.
/// </summary>
/// <remarks>
/// The React bundle ships inside this Razor Class Library under <c>/beacon</c> (configured
/// via <c>StaticWebAssetBasePath</c> in <c>Beacon.UI.csproj</c>). The consuming app must
/// call <see cref="StaticFilesEndpointRouteBuilderExtensions"/>'s <c>UseStaticFiles()</c>
/// in its request pipeline; this extension only wires the SPA fallback.
///
/// The SPA is mounted under <c>/beacon</c> rather than the site root so a host application
/// that owns "/" itself can embed Beacon: the client routes (<c>/home</c>, <c>/queries</c>,
/// <c>/users</c>, ...) used to collide with the host's own routes, and the host won every time.
/// </remarks>
public static class BeaconUiEndpointRouteBuilderExtensions
{
    /// <summary>
    /// The SPA's mount path. Shared with the API (<c>/beacon/api</c>) and MCP
    /// (<c>/beacon/mcp</c>) surfaces, which the fallback pattern below excludes.
    /// </summary>
    public const string BasePath = "/beacon";

    /// <summary>
    /// Default fallback pattern: any path under <c>/beacon/</c> that is not <c>api</c> or
    /// <c>mcp</c> and either has no file extension or ends in a slash. Matches React Router
    /// client-side routes; lets real asset requests (e.g. <c>/beacon/assets/foo.js</c>) fall
    /// through to <c>UseStaticFiles</c>.
    /// </summary>
    public const string DefaultFallbackPattern =
        BasePath + "/{**path:regex(^(?!api(/|$))(?!mcp(/|$))([^.]*|.*/[^./]*)$)}";

    /// <summary>
    /// The authorization policy on the SPA's pages, registered by <c>AddBeaconApiAuthorization()</c> (Beacon.Api): with the
    /// login form on, a signed-out browser is redirected to the login page; with it off, the pages stay open.
    /// </summary>
    public const string PagePolicyName = "BeaconUiPage";

    /// <summary>
    /// The authorization policy on the first-run setup page, registered with <see cref="PagePolicyName"/>: open to a
    /// signed-out browser while Beacon manages users, otherwise like every other page.
    /// </summary>
    public const string SetupPagePolicyName = "BeaconUiSetupPage";

    /// <summary>
    /// The index document's path within the host's web root, which carries this package's
    /// <c>StaticWebAssetBasePath</c> prefix.
    /// </summary>
    private const string IndexFile = "beacon/index.html";

    /// <summary>The pages a signed-out browser reaches: signing in, the page after signing out, and the error page.</summary>
    private static readonly string[] AnonymousPages = ["login", "logout", "error"];

    /// <summary>
    /// Maps the SPA fallback so client-side routes resolve to <c>index.html</c> shipped
    /// by this package. Covers both <c>/beacon</c> and everything beneath it. The pages require
    /// <see cref="PagePolicyName"/>; the login, logout and error pages are anonymous, and the setup page
    /// requires <see cref="SetupPagePolicyName"/>.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The fallback endpoint convention builder, for further configuration.</returns>
    public static IEndpointConventionBuilder MapBeaconUi(this IEndpointRouteBuilder endpoints)
    {
        // The catch-all pattern alone does not match the bare "/beacon" (no trailing segment),
        // which is the URL people actually type, so map that exact path as well.
        endpoints.MapFallbackToFile(BasePath, IndexFile)
            .RequireAuthorization(PagePolicyName);
        MapAuthPages(endpoints);

        return endpoints.MapFallbackToFile(DefaultFallbackPattern, IndexFile)
            .RequireAuthorization(PagePolicyName);
    }

    /// <summary>
    /// Maps the SPA fallback with a custom route pattern. Use when the default pattern's
    /// reserved segments (<c>api</c>, <c>mcp</c>) do not fit the host app. Note that the
    /// bundle's asset URLs and the router's basename are both built against
    /// <see cref="BasePath"/>, so a pattern outside <c>/beacon</c> will not load.
    /// </summary>
    public static IEndpointConventionBuilder MapBeaconUi(
        this IEndpointRouteBuilder endpoints,
        string fallbackPattern)
    {
        MapAuthPages(endpoints);

        return endpoints.MapFallbackToFile(fallbackPattern, IndexFile)
            .RequireAuthorization(PagePolicyName);
    }

    // Exact paths, so they win over the catch-all; matched without regard to case like every route, while the login
    // redirect still sends a differently cased path to the login page (LoginFormAuthMiddleware).
    private static void MapAuthPages(IEndpointRouteBuilder endpoints)
    {
        foreach (var page in AnonymousPages)
        {
            endpoints.MapFallbackToFile($"{BasePath}/{page}", IndexFile)
                .AllowAnonymous();
        }

        endpoints.MapFallbackToFile($"{BasePath}/setup", IndexFile)
            .RequireAuthorization(SetupPagePolicyName);
    }
}
