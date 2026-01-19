using Blazored.LocalStorage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MudBlazor.Services;
using Semantico.Core;
using Semantico.Core.PostgreSql;
using Semantico.Core.SqlServer;
using Semantico.UI.Authentication;
using Semantico.UI.Components;
using Semantico.UI.Components.Shared;

namespace Semantico.UI;

public static class ServiceExtensions
{
    public static IServiceCollection AddSemantico(this IServiceCollection services, IConfiguration configuration, Action<SemanticoConfiguration> semanticoConfiguration)
    {
        var configurationOptions = new SemanticoConfiguration();
        semanticoConfiguration(configurationOptions);
        configurationOptions.Validate();

        // Register database provider based on configuration
        switch (configurationOptions.DatabaseProvider)
        {
            case DatabaseProviderType.PostgreSql:
                services.AddPostgreSqlSemantico(configurationOptions.ConnectionString!, configurationOptions.Schema);
                break;
            case DatabaseProviderType.SqlServer:
                services.AddSqlServerSemantico(configurationOptions.ConnectionString!, configurationOptions.Schema);
                break;
            default:
                throw new InvalidOperationException($"Unsupported database provider: {configurationOptions.DatabaseProvider}");
        }

        services.AddSemanticoCore(configuration, semanticoConfiguration);

        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddMudServices();
        services.AddSingleton<PageHistoryState>();
        services.AddBlazoredLocalStorage();
        services.AddHttpContextAccessor();

        if (configurationOptions.AuthorizationProvider != null)
        {
            services.TryAddSingleton(typeof(ISemanticoAuthorizationProvider), configurationOptions.AuthorizationProvider);
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
                var authorizationProvider = _app.Services.GetService<ISemanticoAuthorizationProvider>();
                if (authorizationProvider != null)
                {
                    semanticoApp.UseMiddleware<SemanticoAuthorizationMiddleware>(authorizationProvider);
                }
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