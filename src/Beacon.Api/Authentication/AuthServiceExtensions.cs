using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
    // The framework's switch that turns off "the only scheme is the default".
    private const string SuppressAutoDefaultSchemeSwitch = "Microsoft.AspNetCore.Authentication.SuppressAutoDefaultScheme";

    private const string SecurityHeadersAddedKey = "Beacon.SecurityHeadersAdded";
    private const string OriginGuardAddedKey = "Beacon.OriginGuardAdded";


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
        // (UseBeaconAuthentication records Beacon's own result, which must not be overridden).
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
                    if (BeaconApiRequest.Is(context.HttpContext))
                    {
                        return BeaconAuthenticationResponses.WriteProblemAsync(context.HttpContext, StatusCodes.Status401Unauthorized);
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    if (BeaconApiRequest.Is(context.HttpContext))
                    {
                        return BeaconAuthenticationResponses.WriteProblemAsync(context.HttpContext, StatusCodes.Status403Forbidden, ApiKeyRefusalDetail(context.HttpContext));
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            });

        // Opt-in stricter cookie: Beacon:Authentication:Cookie:SecureAlways issues the session cookie with the Secure flag
        // whatever the request's scheme (the default stays SameAsRequest). Plain-HTTP listeners outside Development are warned about.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<CookieAuthenticationOptions>, BeaconCookieSecurePolicyConfigurator>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, CookieTransportStartupWarning>());

        // The API-key and bearer schemes and the "Beacon" policy scheme that picks between them and the cookie.
        AddBeaconAuthenticationSchemes(services);

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

        // One authenticator for the bearer scheme: it keeps the refusal-log throttle across requests.
        // The bearer scheme itself is registered by AddBeaconCookieAuthentication, which UseBeaconAuthentication needs.
        services.TryAddSingleton(x => new BeaconJwtAuthenticator(
            x.GetRequiredService<JwtAuthenticationOptions>(),
            x.GetRequiredService<ILogger<BeaconJwtAuthenticationHandler>>()));

        if (!string.IsNullOrEmpty(options.ExternalLoginEndpoint))
        {
            services.Replace(ServiceDescriptor.Scoped<IBeaconAuthenticationProvider>(CreateExternalLoginProvider));
        }

        return services;
    }

    /// <summary>
    /// Registers host-level identity plumbing that depends on the authenticated user: the claims transformer that
    /// materialises Beacon role claims after cookie/OIDC sign-in. The SignalR <c>IUserIdProvider</c>
    /// (<see cref="HubUserIdProvider"/>) is registered once, by <c>AddBeaconApiServices</c>, with realtime on.
    /// The Warp → SignalR job-state bridge is <c>JobStatusChangedBehavior</c>, a Warp pipeline behavior auto-registered
    /// by the source generator. Kept here (§2.12) so <c>Program.cs</c> stays a thin composition root.
    /// </summary>
    public static IServiceCollection AddBeaconHostInfrastructure<TClaimsTransformation>(this IServiceCollection services)
        where TClaimsTransformation : class, Microsoft.AspNetCore.Authentication.IClaimsTransformation
    {
        services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, TClaimsTransformation>();
        return services;
    }

    /// <summary>
    /// Adds the <c>Beacon.Auth</c> cookie sign-in for Beacon's shell at <c>/beacon</c>
    /// (<see cref="AddBeaconCookieAuthentication"/>) and the host plumbing that depends on the signed-in user
    /// (<see cref="AddBeaconHostInfrastructure{TClaimsTransformation}"/>): <typeparamref name="TClaims"/> materialises
    /// Beacon's role claims after a cookie or OIDC sign-in.
    /// </summary>
    public static BeaconBuilder AddCookieAuth<TClaims>(this BeaconBuilder builder)
        where TClaims : class, Microsoft.AspNetCore.Authentication.IClaimsTransformation
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddBeaconHostInfrastructure<TClaims>();
        builder.Services.AddBeaconCookieAuthentication(AuthenticationPathOptions.BeaconPath);

        return builder;
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
    /// Beacon's authentication in the order it needs (§1.9): the host's <c>UseAuthentication</c>, then one Beacon step —
    /// a presented API key or bearer JWT (when enabled), which decides whatever the host's schemes read, else the
    /// <c>Beacon.Auth</c> cookie (<see cref="BeaconAuthenticationDefaults"/>), recorded as the request's authentication
    /// result — then the embed routes' own credentials (when embedding is on), so nothing before it leaves a caller on an
    /// embed path, followed by the Host and Origin check (<c>Beacon:Security</c>). Before them come the security response
    /// headers, unless the host added them earlier with <c>UseBeaconSecurityHeaders</c>, and the request limits. Call it in
    /// place of <c>UseAuthentication</c>, before <c>UseAuthorization</c>. Beacon's step acts on <c>/beacon</c> and
    /// <c>Beacon:Authentication:AdditionalPaths</c> only (<see cref="BeaconRequestPaths"/>), the embed middleware on the
    /// embed routes only (<see cref="BeaconEmbedPaths"/>); every other route keeps the host's own authentication.
    /// </summary>
    public static IApplicationBuilder UseBeaconAuthentication(this IApplicationBuilder app)
    {
        app.UseBeaconSecurityHeaders();
        app.UseMiddleware<BeaconRequestLimitsMiddleware>();
        app.UseAuthentication();
        app.UseMiddleware<BeaconAuthenticationMiddleware>();
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

    // An API key refused by a route that requires a functional permission (its policy names roles a key never carries)
    // is told why; every other refusal keeps the plain body.
    internal static string? ApiKeyRefusalDetail(HttpContext httpContext)
    {
        var functional = httpContext.GetEndpoint()?.Metadata.GetMetadata<BeaconFunctionalPermissionMetadata>();

        return functional is { IsRequired: true } && BeaconUserLookup.IsApiKey(httpContext.User)
            ? BeaconPermissionEndpointFilter.RequiredPermissionDetail(httpContext.User, functional.Permissions)
            : null;
    }

    // Beacon's schemes never take "the only scheme is the default" from the host: when the host has one scheme of its own
    // and names no default, that scheme stays its default, as it was before Beacon's schemes were added.
    private static void AddBeaconAuthenticationSchemes(IServiceCollection services)
    {
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(BeaconAuthenticationDefaults.ApiKeyScheme, null)
            .AddScheme<AuthenticationSchemeOptions, BeaconJwtAuthenticationHandler>(BeaconAuthenticationDefaults.BearerScheme, null)
            .AddPolicyScheme(BeaconAuthenticationDefaults.AuthenticationScheme, null, x => x.ForwardDefaultSelector = BeaconAuthenticationDefaults.SelectScheme);
        services.PostConfigure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(options =>
        {
            if (options.DefaultScheme != null
                || (AppContext.TryGetSwitch(SuppressAutoDefaultSchemeSwitch, out var suppressed) && suppressed))
            {
                return;
            }

            var hostSchemes = options.Schemes
                .Where(x => !BeaconAuthenticationDefaults.RegisteredSchemes.Contains(x.Name))
                .ToList();
            if (hostSchemes.Count == 1)
            {
                options.DefaultScheme = hostSchemes[0].Name;
            }
        });
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
