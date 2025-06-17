using Blazored.LocalStorage;
using MudBlazor.Services;
using Semantico.Core;
using Semantico.UI.Components;
using Semantico.UI.Components.Shared;

namespace Semantico.UI.AspNet;

public static class Helpers
{
    public static IServiceCollection AddSemanticoAdmin(this IServiceCollection services, IConfiguration configuration, Action<SemanticoConfiguration> semanticoConfiguration)
    {
        services.AddSemantico(configuration, semanticoConfiguration);

        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddMudServices();
        services.AddSingleton<PageHistoryState>();
        services.AddBlazoredLocalStorage();

        return services;
    }

    public static IApplicationBuilder UseSemanticoUI(this WebApplication app)
    {
        Semantico.Core.ServiceConfiguration.UseSemantico(app.Services);

        app.MapRazorComponents<SemanticoApp>()
            .AddInteractiveServerRenderMode();

        app.UseAntiforgery();

        app.UseStaticFiles();
        
        return app;
    }
}