using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Semantico.Api.Services;
using Semantico.UI;
using Semantico.Web;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(x =>
    x.SwaggerDoc("semantico", new OpenApiInfo
    {
        Title = "Semantico",
        Version = "v1",
        Description = "Semantico admin endpoints",
        Contact = new OpenApiContact
        {
            Name = "semantico"
        }
    }));

builder.Services.AddHangfire((provider, hangfireConfiguration) => hangfireConfiguration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseFilter(
        new AutomaticRetryAttribute
        {
            Attempts = 0
        })
    .UsePostgreSqlStorage(
        builder.Configuration.GetConnectionString("SemanticoContext"),
        new PostgreSqlStorageOptions
        {
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(1),
        }));
;

//SEMANTICO setup
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<SemanticoScheduler>();
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(x =>
    x.SwaggerEndpoint("/swagger/semantico/swagger.json", "semantico")
);

app.UseHttpsRedirection();

app.UseAuthorization();

app.UseSemanticoApi();

//SEMANTICO admin UI setup
app.UseSemanticoUI();

app.Run();