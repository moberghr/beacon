using Blazored.LocalStorage;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MudBlazor.Services;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.Providers;
using Beacon.Core.Data;
using Beacon.UI.Authentication;
using Beacon.UI.Components;
using Beacon.UI.Components.Shared;

namespace Beacon.UI;

public static class ServiceExtensions
{
    /// <summary>
    /// Adds Beacon UI services (Blazor components and MudBlazor).
    /// Prerequisites: Core services must be registered first using AddBeaconWithPostgreSql or AddBeaconWithSqlServer.
    /// </summary>
    public static IServiceCollection AddBeaconUI(this IServiceCollection services)
    {
        return services.AddBeaconUI(_ => { });
    }

    /// <summary>
    /// Adds Beacon UI services with optional authorization provider.
    /// Prerequisites: Core services must be registered first using AddBeaconWithPostgreSql or AddBeaconWithSqlServer.
    /// </summary>
    public static IServiceCollection AddBeaconUI(this IServiceCollection services, Action<BeaconUIConfiguration> configure)
    {
        var configuration = new BeaconUIConfiguration();
        configure(configuration);

        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddMudServices();
        services.AddSingleton<PageHistoryState>();
        services.AddBlazoredLocalStorage();
        services.AddHttpContextAccessor();

        // Register user context (always available)
        services.TryAddScoped<IBeaconUserContext, HttpContextUserContext>();

        // Register authorization provider (custom or default)
        if (configuration.AuthorizationProvider != null)
        {
            services.TryAddScoped(typeof(Core.Authorization.IBeaconAuthorizationProvider), configuration.AuthorizationProvider);
        }
        else
        {
            services.TryAddScoped<Core.Authorization.IBeaconAuthorizationProvider, DefaultAuthorizationProvider>();
        }

        // Register authentication service
        services.TryAddScoped<IBeaconAuthenticationService, BeaconAuthenticationService>();

        return services;
    }

    /// <summary>
    /// Adds cookie authentication for Beacon login form.
    /// Call this method when using login form authentication.
    /// </summary>
    public static IServiceCollection AddBeaconCookieAuthentication(this IServiceCollection services, string basePath = "/beacon")
    {
        // Register the cookie scheme without changing the host app's default authentication scheme.
        // Beacon routes explicitly authenticate with the cookie scheme via middleware.
        services.AddAuthentication()
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.LoginPath = new PathString($"{basePath}/login");
                options.LogoutPath = new PathString($"{basePath}/logout");
                options.AccessDeniedPath = new PathString($"{basePath}/login");
                options.Cookie.Name = "Beacon.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/beacon/mcp"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            });

        // Persist Data Protection keys to the database so cookies survive app restarts
        // and work across multiple instances
        services.AddDataProtection()
            .SetApplicationName("Beacon")
            .PersistKeysToDbContext<BeaconContext>();

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

        if (string.IsNullOrWhiteSpace(options.Authority)
            || string.IsNullOrWhiteSpace(options.ClientId)
            || string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            throw new InvalidOperationException(
                "Beacon:Authentication:Oidc is Enabled but Authority, ClientId, or ClientSecret is missing.");
        }

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
    /// Supports both login form flow (external API) and bearer token flow.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure JWT options.</param>
    public static IServiceCollection AddBeaconJwtAuthentication(
        this IServiceCollection services,
        Action<JwtAuthenticationOptions> configure)
    {
        var options = new JwtAuthenticationOptions();
        configure(options);

        // Register options as singleton
        services.AddSingleton(options);

        // Register the JWT authentication provider
        // This will be used for both login form flow and bearer token validation
        services.TryAddScoped<JwtExternalApiAuthenticationProvider>();

        // If ExternalLoginEndpoint is configured, register as the authentication provider
        if (!string.IsNullOrEmpty(options.ExternalLoginEndpoint))
        {
            services.AddScoped<IBeaconAuthenticationProvider>(sp =>
                sp.GetRequiredService<JwtExternalApiAuthenticationProvider>());
        }

        return services;
    }

    public static BeaconUIBuilder UseBeaconUI(this WebApplication app)
    {
        return new BeaconUIBuilder(app);
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
}

public class BeaconUIBuilder
{
    private readonly WebApplication _app;
    private readonly BasicAuthConfiguration _basicAuthConfiguration = new();
    private bool _useAuthorization;
    private bool _useLoginForm;
    private bool _useJwtBearer;

    internal BeaconUIBuilder(WebApplication app)
    {
        _app = app;
    }

    public BeaconUIBuilder UseBasicAuthentication(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username cannot be null or empty.", nameof(username));

        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password cannot be null or empty.", nameof(password));

        _basicAuthConfiguration.Enabled = true;
        _basicAuthConfiguration.Username = username;
        _basicAuthConfiguration.Password = password;

        return this;
    }

    public BeaconUIBuilder UseBasicAuthentication(string username, string password, string realm)
    {
        UseBasicAuthentication(username, password);
        _basicAuthConfiguration.Realm = realm;

        return this;
    }

    public BeaconUIBuilder UseAuthorization()
    {
        _useAuthorization = true;
        return this;
    }

    /// <summary>
    /// Enables login form authentication for the Beacon UI.
    /// Requires AddBeaconCookieAuthentication() to be called during service registration
    /// and app.UseAuthentication() to be called before UseBeaconUI().
    /// </summary>
    public BeaconUIBuilder UseLoginForm()
    {
        _useLoginForm = true;
        return this;
    }

    /// <summary>
    /// Enables JWT bearer token authentication for stateless API access.
    /// Validates tokens from Authorization: Bearer {token} headers.
    /// Requires AddBeaconJwtAuthentication() to be called during service registration.
    /// </summary>
    public BeaconUIBuilder UseJwtBearerAuthentication()
    {
        _useJwtBearer = true;
        return this;
    }

    public void AddBlazorUI(string? basePath = null)
    {
        basePath = basePath ?? "/beacon";

        ServiceConfiguration.UseBeacon(_app.Services);

        // Enable static files globally to serve _content files from Razor Class Library
        if (!_app.Environment.IsDevelopment())
        {
            // In production, check if static files middleware is already added
            // This is a workaround since we can't reliably detect if middleware is already added
        }

        // Note: UseStaticFiles should be called on the main app before Map
        // to ensure _content files from RCL are available

        // Get the configuration to check if login form is enabled
        var configuration = _app.Services.GetService<BeaconConfiguration>();

        // Map login/logout API endpoints before the main UI branch (outside Map)
        // This ensures they work with proper HTTP request/response for cookies
        if (_useLoginForm && configuration?.Authentication.EnableLoginForm == true)
        {
            _app.MapLoginEndpoints(basePath, configuration);
        }

        // Map setup endpoints for first-run wizard
        if (configuration?.UserManagement.Enabled == true)
        {
            _app.MapSetupEndpoints(basePath);
        }

        // Get JWT options for bearer authentication
        var jwtOptions = _app.Services.GetService<JwtAuthenticationOptions>();

        // Create a separate pipeline branch for Beacon UI
        _app.Map(basePath, beaconApp =>
        {
            if (_basicAuthConfiguration.Enabled)
            {
                beaconApp.UseMiddleware<BasicAuthMiddleware>(_basicAuthConfiguration);
            }

            // Authenticate Beacon routes with the cookie scheme without affecting the host app's default scheme
            if (_useLoginForm)
            {
                beaconApp.UseMiddleware<BeaconCookieAuthMiddleware>();
            }

            // JWT bearer authentication (stateless) - before login form redirect
            if (_useJwtBearer && jwtOptions?.EnableBearerAuthentication == true)
            {
                beaconApp.UseMiddleware<JwtBearerAuthMiddleware>(jwtOptions);
            }

            if (_useLoginForm && configuration?.Authentication.EnableLoginForm == true)
            {
                // Add login form authentication middleware
                beaconApp.UseMiddleware<LoginFormAuthMiddleware>(configuration, basePath);
            }

            // First-run setup redirect middleware
            if (configuration?.UserManagement.Enabled == true)
            {
                beaconApp.UseMiddleware<FirstRunSetupMiddleware>(configuration, basePath);
            }

            if (_useAuthorization)
            {
                // Add authorization middleware - it will resolve scoped dependencies per request
                beaconApp.UseMiddleware<BeaconAuthorizationMiddleware>();
            }

            beaconApp.UseStaticFiles();
            beaconApp.UseRouting();
            beaconApp.UseAntiforgery();

            beaconApp.UseEndpoints(endpoints =>
            {
                endpoints.MapRazorComponents<BeaconApp>()
                    .AddInteractiveServerRenderMode();
            });
        });
    }
}

public class BeaconUIConfiguration
{
    public Type? AuthorizationProvider { get; set; }

    public void AddAuthorizationProvider<T>() where T : class, Core.Authorization.IBeaconAuthorizationProvider
    {
        AuthorizationProvider = typeof(T);
    }
}