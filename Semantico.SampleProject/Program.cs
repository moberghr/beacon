using Hangfire;
using Hangfire.PostgreSql;
using Semantico.Core.PostgreSql;
using Semantico.Core.SqlServer;
using Semantico.SampleProject.Services;
using Semantico.UI;
using Semantico.UI.AspNet;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel server limits for long-running AI operations
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
    serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
});

// Add services to the container.
// Configure Blazor Server with extended timeouts for long-running AI operations
builder.Services.AddServerSideBlazor()
    .AddCircuitOptions(options =>
    {
        options.DisconnectedCircuitMaxRetained = 100;
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
        options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(5);
        options.MaxBufferedUnacknowledgedRenderBatches = 10;
    });

builder.Services.AddHttpClient().ConfigureHttpClientDefaults(http =>
{
    // Increase timeout for all HTTP clients (including AWS SDK)
    http.ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
    });
});

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
// builder.Services.AddSqlServerSemantico(
//     builder.Configuration.GetConnectionString("SemanticoContextSql")!);

builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<SemanticoScheduler>();
    //options.AddAuthorizationProvider<SampleAuthorizationProvider>();
    // Set the base URL for generating links in notifications (e.g., Teams messages)
    // This should match where your Semantico admin UI is hosted
    options.BaseUrl = "https://localhost:7187/semantico"; // Update with your actual URL
    options.UseAI = true;
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