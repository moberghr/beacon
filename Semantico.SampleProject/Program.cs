using System;
using ApexCharts;
using Blazored.LocalStorage;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using MudBlazor.Services;
using Semantico.Api.Services;
using Semantico.Core;
using Semantico.Core.Data;
using Semantico.UI.Components;
using Semantico.UI.Components.Shared;
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
builder.Services.AddSemantico<SemanticoScheduler>(builder.Configuration);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddSingleton<PageHistoryState>();
builder.Services.AddBlazoredLocalStorage();
builder.Services.AddApexCharts(e =>
{
    e.GlobalOptions = new ApexChartBaseOptions
    {
        Debug = true,
        Theme = new Theme { Palette = PaletteType.Palette6, Mode = Mode.Dark}
    };
});


var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(x =>
    x.SwaggerEndpoint("/swagger/semantico/swagger.json", "semantico")
);

app.UseHttpsRedirection();

app.UseSemantico();
app.UseSemanticoApi();

app.UseAuthorization();

app.UseAntiforgery();


app
    .MapRazorComponents<SemanticoApp>()
    .AddInteractiveServerRenderMode();


app.MapGroup("/xxx")
    .MapRazorComponents<SemanticoApp>()
    .AddInteractiveServerRenderMode();

app.UseStaticFiles();

app.Run();