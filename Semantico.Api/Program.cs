using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Services;
using Semantico.Api.Web;
using Semantico.Core;
using Semantico.Web;
using Semantico.Core.Data;
using Semantico.Api.Hangfire;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerWithApiKey(SemanticoAuth.ApiKeyHeaderName);

builder.Services.AddHangfire(hangfireConfiguration => hangfireConfiguration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options =>
    {
        options.UseNpgsqlConnection(builder.Configuration.GetConnectionString(nameof(SemanticoContext)));
    }, 
    new PostgreSqlStorageOptions
    {
        SchemaName = "semantico_hangfire",
        PrepareSchemaIfNecessary = true
    }));

builder.Services.AddHangfireServer();
builder.Services.AddSemanticoCore<SemanticoScheduler>(builder.Configuration);
builder.Services.AddHttpContextAccessor();

builder.Services.AddAuthentication(SemanticoAuth.ApiKeyHeaderName)
                .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>
                (SemanticoAuth.ApiKeyHeaderName, null);

var app = builder.Build();

using var scope = app.Services.CreateScope();
await scope.ServiceProvider.GetRequiredService<SemanticoContext>().Database.MigrateAsync();

app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers().RequireAuthorization();
app.UseSemanticoUI();

app.MapHangfireDashboard(new DashboardOptions
{
    Authorization = new[]
        {
            new HangfireAuthorizationFilter()
        }
});

app.MapGet("/", () => "OK").ExcludeFromDescription().AllowAnonymous();

app.Run();