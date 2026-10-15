using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Beacon.Api.SignalR;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;

namespace Beacon.Api.Authentication;

/// <summary>
/// Authentication-related service registration helpers, formerly in Beacon.UI.ServiceExtensions.
/// </summary>
public static class AuthServiceExtensions
{
    // A problem body without the members it does not carry (a plain refusal has no detail).
    private static readonly System.Text.Json.JsonSerializerOptions ProblemJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Adds cookie authentication for Beacon login form.
    /// </summary>
    public static IServiceCollection AddBeaconCookieAuthentication(this IServiceCollection services, string basePath = "/")
    {
        services.AddHttpContextAccessor();

        // Register user context. AddBeaconApiServices registers the same pair independently, so
        // cookie and non-cookie hosts both get it; keep them in sync if the implementation changes.
        services.TryAddScoped<IBeaconUserContext, HttpContextUserContext>();

        // Resolves the current principal's internal BeaconUser.Id for audit columns (§1.7 / §9.5).
        services.TryAddScoped<IActorUserResolver, ActorUserResolver>();

        // Register authentication service
        services.TryAddScoped<IBeaconAuthenticationService, BeaconAuthenticationService>();

        // Register the cookie scheme without changing the host app's default authenticate scheme
        // (ApiKeyAuthMiddleware assigns its principal directly and must not be overridden).
        // The challenge/forbid defaults must point at the cookie scheme so anonymous requests to
        // protected endpoints get a 401/403 instead of an InvalidOperationException when other
        // remote schemes (OIDC) are also registered.
        services.AddAuthentication(options =>
        {
            options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultForbidScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.LoginPath = new PathString($"{basePath.TrimEnd('/')}/login");
                options.LogoutPath = new PathString($"{basePath.TrimEnd('/')}/logout");
                options.AccessDeniedPath = new PathString($"{basePath.TrimEnd('/')}/login");
                options.Cookie.Name = "Beacon.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.SlidingExpiration = true;

                // Session lifecycle: the start, the security stamp and the absolute lifetime recorded at sign-in, and the
                // session checked against the user store (Beacon:Authentication:Session); a realtime hub request never
                // renews the cookie. Outside Beacon's routes (BeaconRequestPaths) the events refuse the session, whatever
                // asks the scheme to authenticate there: a host route is never authenticated by the Beacon.Auth cookie.
                options.Events.OnSigningIn = BeaconSessionEvents.SigningInAsync;
                options.Events.OnValidatePrincipal = BeaconSessionEvents.ValidatePrincipalAsync;
                options.Events.OnCheckSlidingExpiration = BeaconSessionEvents.CheckSlidingExpirationAsync;
                options.Events.OnRedirectToLogin = context =>
                {
                    if (IsApiRequest(context))
                    {
                        return WriteJsonStatusAsync(context, StatusCodes.Status401Unauthorized, "Unauthorized");
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    if (IsApiRequest(context))
                    {
                        return WriteJsonStatusAsync(context, StatusCodes.Status403Forbidden, "Forbidden", ApiKeyRefusalDetail(context.HttpContext));
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            });

        // Data Protection, which protects the session cookie, is the host's: Beacon sets no application name and no key
        // storage here (AddBeaconDataProtection is Beacon's default, for a host that asks for it). Outside Development the
        // host fails to start when it persists the key ring without key encryption, keys stored without encryption are
        // revoked and replaced at startup, and a host with no key repository is warned.
        services.AddDataProtectionKeyEncryptionCheck();

        // With local login off, startup fails when nobody could sign in, and warns about break-glass names no user has.
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, LocalLoginStartupCheck>());

        return services;
    }

    public static IServiceCollection AddBeaconOidcAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("Beacon:Authentication:Oidc");
        var options = section.Get<OidcAuthenticationOptions>() ?? new OidcAuthenticationOptions();

        services.Configure<OidcAuthenticationOptions>(section);

        if (!options.Enabled)
        {
            return services;
        }

        // Fails startup without Authority/ClientId/ClientSecret, or without a tenant admission rule.
        options.Validate();

        // Every user provisioned at a first sign-in gets the default role: never the Admin role or a functional role.
        var roleMapping = configuration.GetSection(RoleMappingOptions.SectionName).Get<RoleMappingOptions>() ?? new RoleMappingOptions();
        if (roleMapping.IsPrivilegedRoleName(options.DefaultRoleName))
        {
            throw new InvalidOperationException(
                "Beacon:Authentication:Oidc:DefaultRoleName cannot be the Admin role or the role of a functional permission: every user signing in for the first time would administer Beacon.");
        }

        // Warns at startup when the tenant check is skipped for an Entra authority.
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, OidcStartupWarnings>());

        services.AddAuthentication()
            .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, oidc =>
            {
                oidc.Authority = options.Authority;
                oidc.ClientId = options.ClientId;
                oidc.ClientSecret = options.ClientSecret;
                oidc.CallbackPath = options.CallbackPath;
                oidc.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                oidc.SignOutScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                oidc.ResponseType = OpenIdConnectResponseType.Code;
                oidc.UsePkce = true;
                oidc.SaveTokens = false;
                oidc.GetClaimsFromUserInfoEndpoint = true;
                oidc.MapInboundClaims = false;

                // Front-channel sign-out is an anonymous GET that ends the Beacon session; served only on opt-in.
                if (!options.EnableFrontChannelLogout)
                {
                    oidc.RemoteSignOutPath = PathString.Empty;
                }

                oidc.Scope.Clear();
                foreach (var scope in options.Scopes)
                {
                    oidc.Scope.Add(scope);
                }

                oidc.Events = new OpenIdConnectEvents
                {
                    OnTokenValidated = OidcEventHandlers.HandleTokenValidatedAsync,
                    OnRemoteFailure = OidcEventHandlers.HandleRemoteFailureAsync
                };
            });

        return services;
    }

    /// <summary>
    /// Adds JWT authentication support for Beacon.
    /// </summary>
    public static IServiceCollection AddBeaconJwtAuthentication(
        this IServiceCollection services,
        Action<JwtAuthenticationOptions> configure)
    {
        var options = new JwtAuthenticationOptions();
        configure(options);

        // Validated on the instance that is registered and used: no signing key, issuer or audience fails startup.
        options.Validate();

        services.AddSingleton(options);

        services.TryAddScoped<JwtExternalApiAuthenticationProvider>();

        if (!string.IsNullOrEmpty(options.ExternalLoginEndpoint))
        {
            services.AddScoped<IBeaconAuthenticationProvider>(sp =>
                sp.GetRequiredService<JwtExternalApiAuthenticationProvider>());
        }

        return services;
    }

    /// <summary>
    /// Registers host-level identity and SignalR plumbing that depends on the authenticated
    /// user: the claims transformer that materialises Beacon role claims after cookie/OIDC
    /// sign-in and the SignalR <c>IUserIdProvider</c> that maps connections to
    /// <c>BeaconUser.Id</c>. The Warp → SignalR job-state bridge is <c>JobStatusChangedBehavior</c>,
    /// a Warp pipeline behavior auto-registered by the source generator. Kept here (§2.12) so
    /// <c>Program.cs</c> stays a thin composition root.
    /// </summary>
    public static IServiceCollection AddBeaconHostInfrastructure<TClaimsTransformation>(this IServiceCollection services)
        where TClaimsTransformation : class, Microsoft.AspNetCore.Authentication.IClaimsTransformation
    {
        services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, TClaimsTransformation>();
        services.AddSingleton<IUserIdProvider, HubUserIdProvider>();
        return services;
    }

    public static IApplicationBuilder UseBeaconJwtBearerAuthentication(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetService<JwtAuthenticationOptions>();
        if (options?.EnableBearerAuthentication == true)
        {
            app.UseMiddleware<JwtBearerAuthMiddleware>(options);
        }

        return app;
    }

    /// <summary>
    /// Beacon's authentication in the order it needs (§1.9): API keys, bearer JWTs (when enabled), the host's
    /// <c>UseAuthentication</c>, then the <c>Beacon.Auth</c> cookie. Call it in place of <c>UseAuthentication</c>, before
    /// <c>UseAuthorization</c>. The Beacon middlewares act on <c>/beacon</c> and <c>Beacon:Authentication:AdditionalPaths</c>
    /// only (<see cref="BeaconRequestPaths"/>); every other route keeps the host's own authentication.
    /// </summary>
    public static IApplicationBuilder UseBeaconAuthentication(this IApplicationBuilder app)
    {
        app.UseMiddleware<ApiKeyAuthMiddleware>();
        app.UseBeaconJwtBearerAuthentication();
        app.UseAuthentication();
        app.UseMiddleware<BeaconCookieAuthMiddleware>();

        return app;
    }

    private static bool IsApiRequest(Microsoft.AspNetCore.Authentication.RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/beacon/api") || path.StartsWithSegments("/beacon/mcp"))
        {
            return true;
        }

        var accept = context.Request.Headers.Accept.ToString();
        if (!string.IsNullOrEmpty(accept) && accept.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    // An API key refused by a route that requires a functional permission (its policy names roles a key never carries)
    // is told why; every other refusal keeps the plain body.
    internal static string? ApiKeyRefusalDetail(HttpContext httpContext)
    {
        var functional = httpContext.GetEndpoint()?.Metadata.GetMetadata<BeaconFunctionalPermissionMetadata>();

        return functional is { IsRequired: true } && BeaconUserLookup.IsApiKey(httpContext.User)
            ? BeaconPermissionEndpointFilter.RequiredPermissionDetail(httpContext.User, functional.Permissions)
            : null;
    }

    private static async Task WriteJsonStatusAsync(
        Microsoft.AspNetCore.Authentication.RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> context,
        int statusCode,
        string title,
        string? detail = null)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        var payload = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                type = "about:blank",
                title,
                status = statusCode,
                detail
            },
            ProblemJson);
        await context.Response.WriteAsync(payload, context.HttpContext.RequestAborted);
    }
}