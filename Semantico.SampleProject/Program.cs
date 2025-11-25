using Hangfire;
using Hangfire.PostgreSql;
using Semantico.Core.PostgreSql;
using Semantico.Core.SqlServer;
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

builder.Services.AddHangfireServer();

//SEMANTICO setup - Configure database provider first
 builder.Services.AddPostgreSqlSemantico(
     builder.Configuration.GetConnectionString("SemanticoContext")!, "semantico");

// Uncomment to use SQL Server instead (requires SQL Server connection string)
builder.Services.AddSqlServerSemantico(
    builder.Configuration.GetConnectionString("SemanticoContextSql")!);

builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<SemanticoScheduler>();
    //options.AddAuthorizationProvider<SampleAuthorizationProvider>();
    // Set the base URL for generating links in notifications (e.g., Teams messages)
    // This should match where your Semantico admin UI is hosted
    options.BaseUrl = "https://localhost:7187/semantico"; // Update with your actual URL
});

var app = builder.Build();

app.UseHttpsRedirection();

app.UseAuthorization();

//SEMANTICO admin UI setup
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    //.UseAuthorization()
    .AddBlazorUI("/semantico");

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    IgnoreAntiforgeryToken = true,
});

app.Run();