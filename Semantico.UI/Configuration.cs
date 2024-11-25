using ApexCharts;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Semantico.Core;
using Semantico.UI.Components;
using Semantico.UI.Components.Shared;

namespace Semantico.UI
{
    public static class Configuration
    {
        public static IServiceCollection AddSemanticoAdmin(this IServiceCollection services, IConfiguration configuration, Action<SemanticoConfiguration> semanticoConfiguration)
        {
            services.AddSemantico(configuration, semanticoConfiguration);

            services.AddRazorComponents().AddInteractiveServerComponents();
            services.AddMudServices();
            services.AddSingleton<PageHistoryState>();
            services.AddBlazoredLocalStorage();
            services.AddApexCharts();

            return services;
        }

        public static IApplicationBuilder UseSemanticoUI(this WebApplication app)
        {
            app.UseSemantico();

            app.MapRazorComponents<SemanticoApp>()
                .AddInteractiveServerRenderMode();

            app.UseStaticFiles();

            return app;
        }
    }
}