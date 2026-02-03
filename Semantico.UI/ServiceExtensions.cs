using Blazored.LocalStorage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MudBlazor.Services;
using Semantico.Core;
using Semantico.Core.Authorization;
using Semantico.Core.Authorization.Providers;
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
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddMudServices();
        services.AddSingleton<PageHistoryState>();
        services.AddBlazoredLocalStorage();
        services.AddHttpContextAccessor();

        // Register user context (always available)
        services.TryAddScoped<ISemanticoUserContext, HttpContextUserContext>();

        // Register default authorization provider if not already registered
        services.TryAddScoped<Core.Authorization.ISemanticoAuthorizationProvider, DefaultAuthorizationProvider>();

        return services;
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

        // Create a separate pipeline branch for Semantico UI
        _app.Map(basePath, semanticoApp =>
        {
            if (_basicAuthConfiguration.Enabled)
            {
                semanticoApp.UseMiddleware<BasicAuthMiddleware>(_basicAuthConfiguration);
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