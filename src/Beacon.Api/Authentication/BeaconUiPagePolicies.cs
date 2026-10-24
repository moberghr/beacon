using Beacon.Core;
using Beacon.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Beacon.Api.Authentication;

/// <summary>
/// The authorization policies <c>MapBeaconUi</c> puts on the SPA's pages (<c>BeaconUiEndpointRouteBuilderExtensions</c>
/// names them; Beacon.Api does not reference Beacon.UI, §2.4, so the names are repeated here and a test keeps them equal).
/// With the login form on, a page admits a signed-in caller only, apart from the login page itself, and the setup page
/// also admits a signed-out browser while Beacon manages users; a refused browser is sent to the login page by the
/// cookie scheme's challenge, the same redirect <see cref="LoginFormAuthMiddleware"/> answers. With the login form off,
/// without Beacon's configuration, or outside Beacon's routes (<see cref="BeaconRequestPaths"/>) the pages stay open.
/// </summary>
internal static class BeaconUiPagePolicies
{
    public const string PagePolicyName = "BeaconUiPage";

    public const string SetupPagePolicyName = "BeaconUiSetupPage";

    public static void AddTo(Microsoft.AspNetCore.Authorization.AuthorizationOptions options)
    {
        options.AddPolicy(PagePolicyName, new AuthorizationPolicyBuilder()
            .RequireAssertion(x => Admits(x, setupPage: false))
            .Build());
        options.AddPolicy(SetupPagePolicyName, new AuthorizationPolicyBuilder()
            .RequireAssertion(x => Admits(x, setupPage: true))
            .Build());
    }

    /// <summary>Whether the request's endpoint is one of the SPA's pages (it names one of these policies).</summary>
    public static bool IsPage(HttpContext context)
    {
        var authorizeData = context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>() ?? [];

        return authorizeData.Any(x => x.Policy is PagePolicyName or SetupPagePolicyName);
    }

    private static bool Admits(AuthorizationHandlerContext context, bool setupPage)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return true;
        }

        // Endpoint routing always passes the request; without it there is nothing to decide by, so a signed-out caller is refused.
        if (context.Resource is not HttpContext httpContext)
        {
            return false;
        }

        var configuration = httpContext.RequestServices.GetService<BeaconConfiguration>();
        if (configuration is not { Authentication.EnableLoginForm: true } || !BeaconRequestPaths.IsBeaconRequest(httpContext))
        {
            return true;
        }

        return setupPage
            ? configuration.UserManagement.Enabled
            : BeaconLoginPage.IsRequestFor(httpContext.Request.Path, AuthenticationPathOptions.BeaconPath, configuration);
    }
}

/// <summary>The login page a signed-out browser is sent to: the configured <c>LoginPath</c> under the UI's base path.</summary>
internal static class BeaconLoginPage
{
    public static string PathFor(string basePath, BeaconConfiguration configuration)
    {
        return $"{basePath.TrimEnd('/')}{configuration.Authentication.LoginPath}";
    }

    /// <summary>
    /// Whether the request is for the login page itself, which is never redirected to itself. Exact and Ordinal: the SPA's
    /// router is case-sensitive, so a differently cased path is sent to the login page instead.
    /// </summary>
    public static bool IsRequestFor(PathString path, string basePath, BeaconConfiguration configuration)
    {
        return configuration.Authentication.LoginPath.Length > 1
            && string.Equals(path.Value, PathFor(basePath, configuration), StringComparison.Ordinal);
    }
}
