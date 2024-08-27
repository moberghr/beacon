using Microsoft.AspNetCore.Builder;
using Semantico.Core;
using Semantico.Web.Endpoints;

namespace Semantico.Web
{
    public static class ServiceConfiguration
    {
        public static IApplicationBuilder UseSemanticoApi(this WebApplication app) 
        {
            app.UseSemantico();
            app.MapSemanticoEndpoints();

            return app;
        }
    }
}
