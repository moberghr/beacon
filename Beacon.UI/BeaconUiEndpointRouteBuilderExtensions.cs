using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Beacon.UI;

/// <summary>
/// Hosts the Beacon React SPA from this assembly's static web assets.
/// </summary>
/// <remarks>
/// The React bundle ships inside this Razor Class Library at the root path (configured
/// via <c>StaticWebAssetBasePath</c> in <c>Beacon.UI.csproj</c>). The consuming app must
/// call <see cref="StaticFilesEndpointRouteBuilderExtensions"/>'s <c>UseStaticFiles()</c>
/// in its request pipeline; this extension only wires the SPA fallback.
/// </remarks>
public static class BeaconUiEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Default fallback pattern: any path that does not start with <c>/beacon/</c> or
    /// <c>/hangfire/</c> and either has no file extension or ends in a slash. Matches
    /// React Router client-side routes; lets real asset requests (e.g. <c>/assets/foo.js</c>)
    /// fall through to <c>UseStaticFiles</c>.
    /// </summary>
    public const string DefaultFallbackPattern =
        "/{**path:regex(^(?!beacon(/|$))(?!hangfire(/|$))([^.]*|.*/[^./]*)$)}";

    /// <summary>
    /// Maps the SPA fallback so client-side routes resolve to <c>index.html</c> shipped
    /// by this package.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The fallback endpoint convention builder, for further configuration.</returns>
    public static IEndpointConventionBuilder MapBeaconUi(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapFallbackToFile(DefaultFallbackPattern, "index.html");
    }

    /// <summary>
    /// Maps the SPA fallback with a custom route pattern. Use when the default pattern's
    /// reserved prefixes (<c>/beacon</c>, <c>/hangfire</c>) do not fit the host app.
    /// </summary>
    public static IEndpointConventionBuilder MapBeaconUi(
        this IEndpointRouteBuilder endpoints,
        string fallbackPattern)
    {
        return endpoints.MapFallbackToFile(fallbackPattern, "index.html");
    }
}
