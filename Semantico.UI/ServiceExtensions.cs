using Blazored.LocalStorage;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MudBlazor.Services;
using Semantico.Core;
using Semantico.Core.Authentication;
using Semantico.Core.Authentication.Providers;
using Semantico.Core.Authorization;
using Semantico.Core.Authorization.Providers;
using Semantico.Core.Data;
using Semantico.UI.Authentication;
using Semantico.UI.Components;
using Semantico.UI.Components.Shared;

namespace Semantico.UI;

public static class ServiceExtensions
{
    /// <summary>
    /// Adds Semantico UI services (Blazor components and MudBlazor).
    /// Prerequisites: Core services must be registered first using AddSemanticoWithPostgreSql or AddSemanticoWithSqlServer.
    /// </summary>
    public static IServiceCollection AddSemanticoUI(this IServiceCollection services)
    {
        return services.AddSemanticoUI(_ => { });
    }

    /// <summary>
    /// Adds Semantico UI services with optional authorization provider.
    /// Prerequisites: Core services must be registered first using AddSemanticoWithPostgreSql or AddSemanticoWithSqlServer.
    /// </summary>
    public static IServiceCollection AddSemanticoUI(this IServiceCollection services, Action<SemanticoUIConfiguration> configure)
    {
        var configuration = new SemanticoUIConfiguration();
        configure(configuration);

        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddMudServices();
        services.AddSingleton<PageHistoryState>();
        services.AddBlazoredLocalStorage();
        services.AddHttpContextAccessor();

        // Register user context (always available)
        services.TryAddScoped<ISemanticoUserContext, HttpContextUserContext>();

        // Register authorization provider (custom or default)
        if (configuration.AuthorizationProvider != null)
        {
            services.TryAddScoped(typeof(Core.Authorization.ISemanticoAuthorizationProvider), configuration.AuthorizationProvider);
        }
        else
        {
            services.TryAddScoped<Core.Authorization.ISemanticoAuthorizationProvider, DefaultAuthorizationProvider>();
        }

        // Register authentication service
        services.TryAddScoped<ISemanticoAuthenticationService, SemanticoAuthenticationService>();

        return services;
    }

    /// <summary>
    /// Adds cookie authentication for Semantico login form.
    /// Call this method when using login form authentication.
    /// </summary>
    public static IServiceCollection AddSemanticoCookieAuthentication(this IServiceCollection services, string basePath = "/semantico")
    {
        // Register the cookie scheme without changing the host app's default authentication scheme.
        // Semantico routes explicitly authenticate with the cookie scheme via middleware.
        services.AddAuthentication()
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.LoginPath = new PathString($"{basePath}/login");
                options.LogoutPath = new PathString($"{basePath}/logout");
                options.AccessDeniedPath = new PathString($"{basePath}/login");
                options.Cookie.Name = "Semantico.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/semantico/mcp"))
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
            .SetApplicationName("Semantico")
            .PersistKeysToDbContext<SemanticoContext>();

        return services;
    }

    /// <summary>
    /// Adds JWT authentication support for Semantico.
    /// Supports both login form flow (external API) and bearer token flow.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure JWT options.</param>
    public static IServiceCollection AddSemanticoJwtAuthentication(
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
            services.AddScoped<ISemanticoAuthenticationProvider>(sp =>
                sp.GetRequiredService<JwtExternalApiAuthenticationProvider>());
        }

        return services;
    }

    public static SemanticoUIBuilder UseSemanticoUI(this WebApplication app)
    {
        return new SemanticoUIBuilder(app);
    }
}

public class SemanticoUIBuilder
{
    private readonly WebApplication _app;
    private readonly BasicAuthConfiguration _basicAuthConfiguration = new();
    private bool _useAuthorization;
    private bool _useLoginForm;
    private bool _useJwtBearer;

    internal SemanticoUIBuilder(WebApplication app)
    {
        _app = app;
    }

    public SemanticoUIBuilder UseBasicAuthentication(string username, string password)
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

    public SemanticoUIBuilder UseBasicAuthentication(string username, string password, string realm)
    {
        UseBasicAuthentication(username, password);
        _basicAuthConfiguration.Realm = realm;

        return this;
    }

    public SemanticoUIBuilder UseAuthorization()
    {
        _useAuthorization = true;
        return this;
    }

    /// <summary>
    /// Enables login form authentication for the Semantico UI.
    /// Requires AddSemanticoCookieAuthentication() to be called during service registration
    /// and app.UseAuthentication() to be called before UseSemanticoUI().
    /// </summary>
    public SemanticoUIBuilder UseLoginForm()
    {
        _useLoginForm = true;
        return this;
    }

    /// <summary>
    /// Enables JWT bearer token authentication for stateless API access.
    /// Validates tokens from Authorization: Bearer {token} headers.
    /// Requires AddSemanticoJwtAuthentication() to be called during service registration.
    /// </summary>
    public SemanticoUIBuilder UseJwtBearerAuthentication()
    {
        _useJwtBearer = true;
        return this;
    }

    public void AddBlazorUI(string? basePath = null)
    {
        basePath = basePath ?? "/semantico";

        ServiceConfiguration.UseSemantico(_app.Services);

        // Enable static files globally to serve _content files from Razor Class Library
        if (!_app.Environment.IsDevelopment())
        {
            // In production, check if static files middleware is already added
            // This is a workaround since we can't reliably detect if middleware is already added
        }

        // Note: UseStaticFiles should be called on the main app before Map
        // to ensure _content files from RCL are available

        // Get the configuration to check if login form is enabled
        var configuration = _app.Services.GetService<SemanticoConfiguration>();

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

        // Create a separate pipeline branch for Semantico UI
        _app.Map(basePath, semanticoApp =>
        {
            if (_basicAuthConfiguration.Enabled)
            {
                semanticoApp.UseMiddleware<BasicAuthMiddleware>(_basicAuthConfiguration);
            }

            // Authenticate Semantico routes with the cookie scheme without affecting the host app's default scheme
            if (_useLoginForm)
            {
                semanticoApp.UseMiddleware<SemanticoCookieAuthMiddleware>();
            }

            // JWT bearer authentication (stateless) - before login form redirect
            if (_useJwtBearer && jwtOptions?.EnableBearerAuthentication == true)
            {
                semanticoApp.UseMiddleware<JwtBearerAuthMiddleware>(jwtOptions);
            }

            if (_useLoginForm && configuration?.Authentication.EnableLoginForm == true)
            {
                // Add login form authentication middleware
                semanticoApp.UseMiddleware<LoginFormAuthMiddleware>(configuration, basePath);
            }

            // First-run setup redirect middleware
            if (configuration?.UserManagement.Enabled == true)
            {
                semanticoApp.UseMiddleware<FirstRunSetupMiddleware>(configuration, basePath);
            }

            if (_useAuthorization)
            {
                // Add authorization middleware - it will resolve scoped dependencies per request
                semanticoApp.UseMiddleware<SemanticoAuthorizationMiddleware>();
            }

            semanticoApp.UseStaticFiles();
            semanticoApp.UseRouting();
            semanticoApp.UseAntiforgery();

            semanticoApp.UseEndpoints(endpoints =>
            {
                endpoints.MapRazorComponents<SemanticoApp>()
                    .AddInteractiveServerRenderMode();
            });
        });
    }
}

public class SemanticoUIConfiguration
{
    public Type? AuthorizationProvider { get; set; }

    public void AddAuthorizationProvider<T>() where T : class, Core.Authorization.ISemanticoAuthorizationProvider
    {
        AuthorizationProvider = typeof(T);
    }
}