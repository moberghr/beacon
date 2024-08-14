using Microsoft.AspNetCore.Builder;
using Semantico.Web.Endpoints;

namespace Semantico.Web
{
    public static class ServiceConfiguration
    {
        public static IApplicationBuilder UseSemanticoUI(this WebApplication app) 
        { 
            app.MapSemanticoEndpoints();

            return app;
        }
    }
}
