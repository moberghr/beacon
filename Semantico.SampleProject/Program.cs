using Hangfire;
using Hangfire.PostgreSql;
using Semantico.AI;
using Semantico.Core;
using Semantico.Core.PostgreSql;
using Semantico.Core.SqlServer;
using Semantico.SampleProject.Services;
using Semantico.UI;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel server limits for long-running AI operations
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
    serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
});

// Configure HTTP clients with extended timeouts for AI operations
builder.Services.AddHttpClient().ConfigureHttpClientDefaults(http =>
{
    http.ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
    });
});

// Configure Hangfire with PostgreSQL
builder.Services.AddHangfire(hangfireConfiguration => hangfireConfiguration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseFilter(new AutomaticRetryAttribute { Attempts = 0 })
    .UsePostgreSqlStorage(
        builder.Configuration.GetConnectionString("SemanticoContext"),
        new PostgreSqlStorageOptions
        {
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(1),
        }));

builder.Services.AddHangfireServer();

// ============================================================================
// SEMANTICO SETUP
// ============================================================================

// Step 1: Add Semantico core services with database provider

builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<SemanticoScheduler>();
        options.BaseUrl = "https://localhost:7187/semantico"; // For notification links
        options.UseAI = true; // Enable AI features (requires LLM configuration)

        // Enable authorization with role-based access control
        options.Authorization.Enabled = true;
        options.AddAuthorizationProvider<SampleAuthorizationProvider>();

        // Enable login form authentication
        options.Authentication.EnableLoginForm = true;
        options.AddAuthenticationProvider<SampleAuthenticationProvider>();
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("SemanticoContext")!, "semantico")
    //.UseSqlServer(builder.Configuration.GetConnectionString("SemanticoContextSql")!, "semantico")
    ;

// Alternative: Use SQL Server instead of PostgreSQL
// builder.Services.AddSemanticoWithSqlServer(
//     builder.Configuration,
//     builder.Configuration.GetConnectionString("SemanticoContextSql")!,
//     "semantico",
//     options =>
//     {
//         options.AddSemanticoScheduler<SemanticoScheduler>();
//         options.BaseUrl = "https://localhost:7187/semantico";
//         options.UseAI = true;
//     });

// Step 2: Add Semantico UI components (Blazor + MudBlazor)
builder.Services.AddSemanticoUI();

// Step 3: Add cookie authentication for login form
builder.Services.AddSemanticoCookieAuthentication("/semantico");

// Register claims transformer to add role claims after authentication
builder.Services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, SampleClaimsTransformation>();

// Step 4: Add Semantico AI services (required for AI features)
builder.Services.AddSemanticoAI(builder.Configuration);

var app = builder.Build();

// ============================================================================
// MIDDLEWARE PIPELINE
// ============================================================================

app.UseHttpsRedirection();

// IMPORTANT: UseStaticFiles must be called before UseSemanticoUI
// to serve _content files from Razor Class Libraries
app.UseStaticFiles();

// Authentication must be before authorization
app.UseAuthentication();
app.UseAuthorization();

// Semantico Admin UI - available at /semantico
// Using login form instead of basic authentication
app.UseSemanticoUI()
    .UseLoginForm() // Login form for demo (use admin/admin, editor/editor, or viewer/viewer)
    .UseAuthorization() // Enable authorization checks
    .AddBlazorUI("/semantico");

// Hangfire Dashboard - available at /hangfire
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    IgnoreAntiforgeryToken = true,
});

app.Run();