using Microsoft.OpenApi.Models;
using System.Reflection;

namespace Semantico.Api.Helpers;

public static class SwaggerHelper
{
    public static void AddSwaggerWithApiKey(this IServiceCollection services, string apiKeyName)
    {
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo {
                Title = "Semantico",
                Version = "v1",
                Description = $"For Authentication use {apiKeyName} header. Example: \"{apiKeyName} 00000000-0000-0000-0000-000000000000\""
            });

            var securityScheme = new OpenApiSecurityScheme
            {
                Name = apiKeyName,
                Type = SecuritySchemeType.ApiKey,
                Scheme = apiKeyName,
                In = ParameterLocation.Header,
                Description = "Example: \"00000000-0000-0000-0000-000000000000\"",
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = apiKeyName
                }
            };

            options.AddSecurityDefinition(apiKeyName, securityScheme);

            options.AddSecurityRequirement(new OpenApiSecurityRequirement
              {
                  {
                      securityScheme,
                      Array.Empty<string>()
                  }
              });
        });
    }
}
