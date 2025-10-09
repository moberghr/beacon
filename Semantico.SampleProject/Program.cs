using Hangfire;
using Hangfire.PostgreSql;
using Semantico.SampleProject.Services;
using Semantico.UI;
using Semantico.UI.AspNet;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

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


//builder.Services.AddHangfireServer();

//SEMANTICO setup
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<SemanticoScheduler>();
    options.AddAuthorizationProvider<SampleAuthorizationProvider>();
});

var app = builder.Build();

app.UseHttpsRedirection();

app.UseAuthorization();

//SEMANTICO admin UI setup
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .UseAuthorization()
    .AddBlazorUI();

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    IgnoreAntiforgeryToken = true,
});

app.Run();