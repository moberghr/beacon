using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Beacon.Api.Security;
using Beacon.Api.SignalR;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Http;

namespace Beacon.Api.Authentication;

/// <summary>
/// Authentication-related service registration helpers, formerly in Beacon.UI.ServiceExtensions.
/// </summary>
public static class AuthServiceExtensions
{
    private const string SecurityHeadersAddedKey = "Beacon.SecurityHeadersAdded";
    private const string OriginGuardAddedKey = "Beacon.OriginGuardAdded";

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

        // The request's caller as Core reads it (see AddBeaconApiServices); replaces only Core's off-request default.
        services.TryUseRequestPrincipal<Beacon.Shared.Http.HttpContextRequestPrincipal>();
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, RequestPrincipalStartupCheck>());

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
                        // An anonymous request to a Beacon route is counted against its address's limit of refused
                        // anonymous requests (Beacon:RateLimits:Anonymous): over it, 429 instead of the 401.
                        if (!AnonymousRefusalThrottle.TryAdmit(context.HttpContext, out var retryAfter))
                        {
                            return RateLimitRefusal.WriteAsync(context.HttpContext, retryAfter);
                        }

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

        // Opt-in stricter cookie: Beacon:Authentication:Cookie:SecureAlways issues the session cookie with the Secure flag
        // whatever the request's scheme (the default stays SameAsRequest). Plain-HTTP listeners outside Development are warned about.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<CookieAuthenticationOptions>, BeaconCookieSecurePolicyConfigurator>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, CookieTransportStartupWarning>());

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
        var options = OidcAuthenticationOptions.FromConfiguration(configuration);

        services.Configure<OidcAuthenticationOptions>(configuration.GetSection(OidcAuthenticationOptions.SectionName));

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
                $"{OidcAuthenticationOptions.SectionName}:{nameof(OidcAuthenticationOptions.DefaultRoleName)} cannot be the Admin role or the role of a functional permission: every user signing in for the first time would administer Beacon.");
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
    /// Adds JWT authentication support for Beacon. With an <see cref="JwtAuthenticationOptions.ExternalLoginEndpoint"/>,
    /// the password sign-in provider becomes <see cref="HybridAuthenticationProvider"/>, whichever order this and
    /// <c>AddBeaconServices</c> are called in: internal users sign in first and the external login is the fallback, so
    /// adding it never takes internal-password sign-in away. Without user management, or with internal users off
    /// (<see cref="UserManagementOptions.AllowInternalUsers"/>), the external login alone is the provider. An <c>http</c>
    /// JWKS or login URL to a loopback host is accepted in Development only (startup fails otherwise). The JWKS fetch
    /// uses Beacon's own bounded named client (<see cref="BeaconHttpClients.Jwks"/>): to change its proxy or handler, configure
    /// that named client (<c>AddHttpClient(BeaconHttpClients.Jwks)</c>); an <c>AddHttpClient&lt;JwtExternalApiAuthenticationProvider&gt;</c>
    /// registered after this call is not used, and one registered before it takes the provider's registration.
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
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, JwtEndpointStartupCheck>());

        // The JWKS fetch goes through its own bounded client (10 s, 256 KB, no redirects), not the factory's default one.
        services.AddBeaconOutboundHttpClients();
        services.TryAddScoped(CreateJwtProvider);

        if (!string.IsNullOrEmpty(options.ExternalLoginEndpoint))
        {
            services.Replace(ServiceDescriptor.Scoped<IBeaconAuthenticationProvider>(CreateExternalLoginProvider));
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
    /// The embed middleware, only while embedding is on (<c>Beacon:Embed:Enabled</c>, never in gateway mode): a host without
    /// it registers nothing, and every embed path is then an ordinary Beacon path that answers 404.
    /// </summary>
    public static IApplicationBuilder UseBeaconEmbedAuthentication(this IApplicationBuilder app)
    {
        if (BeaconEmbedPaths.IsEnabled(app.ApplicationServices))
        {
            app.UseMiddleware<EmbedTokenAuthMiddleware>();
        }

        return app;
    }

    /// <summary>
    /// Beacon's authentication in the order it needs (§1.9): API keys, bearer JWTs (when enabled), the host's
    /// <c>UseAuthentication</c>, the <c>Beacon.Auth</c> cookie, then the embed routes' own credentials (when embedding is
    /// on), so nothing before it leaves a caller on an embed path, followed by the Host and Origin check
    /// (<c>Beacon:Security</c>). Before them come the security response headers, unless the host added them earlier with
    /// <c>UseBeaconSecurityHeaders</c>. Call it in place of <c>UseAuthentication</c>, before <c>UseAuthorization</c>. The
    /// Beacon middlewares act on <c>/beacon</c> and <c>Beacon:Authentication:AdditionalPaths</c> only
    /// (<see cref="BeaconRequestPaths"/>), the embed middleware on the embed routes only (<see cref="BeaconEmbedPaths"/>);
    /// every other route keeps the host's own authentication.
    /// </summary>
    public static IApplicationBuilder UseBeaconAuthentication(this IApplicationBuilder app)
    {
        app.UseBeaconSecurityHeaders();
        app.UseMiddleware<BeaconRequestLimitsMiddleware>();
        app.UseMiddleware<ApiKeyAuthMiddleware>();
        app.UseBeaconJwtBearerAuthentication();
        app.UseAuthentication();
        app.UseMiddleware<BeaconCookieAuthMiddleware>();
        app.UseBeaconEmbedAuthentication();

        // After authentication: a remote sign-in callback (an OIDC form post from the identity provider) is answered by
        // UseAuthentication and never reaches the check.
        app.UseBeaconOriginGuard();

        return app;
    }

    /// <summary>
    /// The security response headers on Beacon's routes (<c>Beacon:Security</c>, <see cref="BeaconHostSecurityOptions"/>).
    /// <see cref="UseBeaconAuthentication"/> adds them when the host has not; call this earlier, before <c>UseStaticFiles</c>,
    /// for the SPA's static files to carry them too. Added once, however often it is called.
    /// </summary>
    public static IApplicationBuilder UseBeaconSecurityHeaders(this IApplicationBuilder app)
    {
        if (app.Properties.ContainsKey(SecurityHeadersAddedKey))
        {
            return app;
        }

        app.Properties[SecurityHeadersAddedKey] = true;

        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }

    /// <summary>
    /// The Host and Origin check on Beacon's routes (<c>Beacon:Security</c>, <see cref="BeaconHostSecurityOptions"/>).
    /// <see cref="UseBeaconAuthentication"/> adds it after authentication; a host that does not use that (the MCP gateway)
    /// adds it with this. An OpenID Connect callback, sign-out callback or remote sign-out path is never refused, wherever
    /// the check sits. Added once, however often it is called.
    /// </summary>
    public static IApplicationBuilder UseBeaconOriginGuard(this IApplicationBuilder app)
    {
        if (app.Properties.ContainsKey(OriginGuardAddedKey))
        {
            return app;
        }

        app.Properties[OriginGuardAddedKey] = true;

        return app.UseMiddleware<OriginGuardMiddleware>();
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

    private static JwtExternalApiAuthenticationProvider CreateJwtProvider(IServiceProvider services)
    {
        var httpClient = services.GetRequiredService<IHttpClientFactory>().CreateClient(BeaconHttpClients.Jwks);
        var options = services.GetRequiredService<JwtAuthenticationOptions>();
        var logger = services.GetRequiredService<ILogger<JwtExternalApiAuthenticationProvider>>();
        var users = services.GetService<Beacon.Core.Services.IUserManagementService>();
        var oidc = services.GetService<IOptions<OidcAuthenticationOptions>>();
        var configuration = services.GetService<BeaconConfiguration>();

        return configuration == null
            ? new JwtExternalApiAuthenticationProvider(httpClient, options, logger, users, oidc)
            : new JwtExternalApiAuthenticationProvider(httpClient, options, logger, configuration, users, oidc);
    }

    // Internal users first, the external login as the fallback (HybridAuthenticationProvider), whenever Beacon holds
    // internal users; the external login alone otherwise (it fails every login without user management).
    private static IBeaconAuthenticationProvider CreateExternalLoginProvider(IServiceProvider services)
    {
        var jwtProvider = services.GetRequiredService<JwtExternalApiAuthenticationProvider>();
        var users = services.GetService<Beacon.Core.Services.IUserManagementService>();
        var configuration = services.GetService<BeaconConfiguration>();
        if (users == null || configuration is not { UserManagement.AllowInternalUsers: true })
        {
            return jwtProvider;
        }

        return new HybridAuthenticationProvider(users, jwtProvider, configuration);
    }
}
