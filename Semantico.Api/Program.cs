using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Services;
using Semantico.Core;
using Semantico.Web;
using Semantico.Api.Hangfire;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Semantico",
        Version = "v1",
    });
});

builder.Services.AddHangfire(hangfireConfiguration => hangfireConfiguration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options =>
    {
        options.UseNpgsqlConnection(builder.Configuration.GetConnectionString("SemanticoContext"));
    }, 
    new PostgreSqlStorageOptions
    {
        SchemaName = "semantico_hangfire",
        PrepareSchemaIfNecessary = true
    }));

builder.Services.AddHangfireServer();

// register semantico services here
builder.Services.AddSemantico<SemanticoScheduler>(builder.Configuration);

builder.Services.AddHttpContextAccessor();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// use semantico
app.UseSemantico();
// or use semantico with endpoints
app.UseSemanticoApi();

app.MapHangfireDashboard(new DashboardOptions
{
    Authorization = new[]
        {
            new HangfireAuthorizationFilter()
        }
});

app.MapGet("/", () => "OK").ExcludeFromDescription().AllowAnonymous();

app.Run();