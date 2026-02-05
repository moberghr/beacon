using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Semantico.Core.Adapters;
using Semantico.Core.Adapters.Jira;
using Semantico.Core.Adapters.Mail;
using Semantico.Core.Adapters.Slack;
using Semantico.Core.Adapters.Teams;
using Semantico.Core.Authentication;
using Semantico.Core.Authentication.Providers;
using Semantico.Core.Authorization;
using Semantico.Core.Authorization.Providers;
using Semantico.Core.Data;
using Semantico.Core.Models;
using Semantico.Core.Services;
using Semantico.Core.Services.Shared;
using Semantico.Core.Worker;
using Semantico.Core.Worker.Repositories;
using Semantico.Core.Worker.Services;

namespace Semantico.Core;

public static class ServiceConfiguration
{
    /// <summary>
    /// Adds core Semantico services (without UI components).
    /// Chain with .UsePostgreSql() or .UseSqlServer() to configure the database provider.
    /// </summary>
    public static SemanticoBuilder AddSemanticoServices(this IServiceCollection services, IConfiguration configuration, Action<SemanticoConfiguration> semanticoConfiguration)
    {
        var configurationOptions = new SemanticoConfiguration();
        semanticoConfiguration(configurationOptions);
        configurationOptions.ValidateCore();

        // Register configuration for access by adapters and other services
        services.AddSingleton(configurationOptions);

        services.AddHttpClient();
        services.AddMemoryCache();

        // Note: ISemanticoUserContext and ISemanticoAuthorizationProvider are registered by UI layer
        // Core only provides the interfaces and default implementations

        // Register authorization provider
        if (configurationOptions.Authorization.ProviderType != null)
        {
            services.TryAddScoped(
                typeof(ISemanticoAuthorizationProvider),
                configurationOptions.Authorization.ProviderType);
        }
        else
        {
            // Default: allow all (backward compatible)
            services.TryAddScoped<ISemanticoAuthorizationProvider, DefaultAuthorizationProvider>();
        }

        // Register authentication provider
        if (configurationOptions.Authentication.ProviderType != null)
        {
            services.TryAddScoped(
                typeof(ISemanticoAuthenticationProvider),
                configurationOptions.Authentication.ProviderType);
        }
        else
        {
            // Default: authentication fails (requires explicit configuration)
            services.TryAddScoped<ISemanticoAuthenticationProvider, DefaultAuthenticationProvider>();
        }

        // MediatR for CQRS pattern
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ServiceConfiguration).Assembly));

        // Encryption service for sensitive data (e.g., connection strings)
        var encryptionKey = configuration["Semantico:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(encryptionKey))
        {
            throw new InvalidOperationException(
                "Semantico:EncryptionKey must be configured. " +
                "Generate a secure key with: openssl rand -base64 32" +
                Environment.NewLine +
                "Then add to appsettings.json: { \"Semantico\": { \"EncryptionKey\": \"your-generated-key\" } }");
        }
        services.AddSingleton<IEncryptionService>(new EncryptionService(encryptionKey));

        services.AddSingleton<IAdapter, TeamsAdapter>();
        services.AddSingleton<IAdapter, SlackAdapter>();
        if (configurationOptions.EmailAdapter != null)
        {
            services.TryAddSingleton(typeof(IEmailAdapter), configurationOptions.EmailAdapter);
            services.AddSingleton<IAdapter, EmailAdapter>();
        }
        services.TryAddSingleton<IJiraRestClientFactory, JiraRestClientFactory>();
        services.TryAddSingleton<IJiraApiAdapter, JiraApiAdapter>();
        services.AddSingleton<IAdapter, JiraAdapter>();
        services.TryAddSingleton<AdapterFactory>();

        // Shared services (for use by Query and Migration features)
        services.TryAddTransient<QueryExecutionOrchestrator>();
        services.TryAddTransient<ParameterResolver>();
        services.TryAddTransient<SchedulingService>();

        // Data source providers (use AddTransient to register multiple implementations)
        services.AddTransient<Services.Providers.IDataSourceProvider, Services.Providers.DatabaseProvider>();
        services.AddTransient<Services.Providers.IDataSourceProvider, Services.Providers.CloudWatchProvider>();
        services.TryAddTransient<Services.Providers.IDataSourceProviderFactory, Services.Providers.DataSourceProviderFactory>();

        // Domain services
        services.TryAddTransient<IJobRepository, JobRepository>();
        services.TryAddTransient<IJobService, JobService>();
        services.TryAddTransient<INotificationService, NotificationService>();
        services.TryAddTransient<IDataSourceService, DataSourceService>();
        services.TryAddTransient<IQueryService, QueryService>();
        services.TryAddTransient<IQueryExecutionPreviewService, QueryExecutionPreviewService>();
        services.TryAddTransient<ISubscriptionService, SubscriptionService>();
        services.TryAddTransient<IRecipientService, RecipientService>();
        services.TryAddTransient<ITaskService, TaskService>();
        services.TryAddTransient<IStatisticsService, StatisticsService>();
        services.TryAddTransient<IMigrationService, MigrationService>();
        services.TryAddTransient<IDatabaseMetadataService, DatabaseMetadataService>();
        services.TryAddTransient<IAnomalyDetectionService, AnomalyDetectionService>();
        services.TryAddTransient<IManualQueryExecutionLogger, ManualQueryExecutionLogger>();

        services.TryAddTransient(typeof(ISemanticoScheduler), configurationOptions.SemanticoScheduler!);

        return new SemanticoBuilder(services, configuration);
    }

    public static void UseSemantico(IServiceProvider serviceProvider, bool createSchema = false)
    {
        using var scope = serviceProvider.CreateScope();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SemanticoContext>>();
        using var context = contextFactory.CreateDbContext();

        // Get the schema name from the context
        var schema = GetSchemaFromContext(context);

        // Ensure the schema exists before running migrations
        if (createSchema)
        {
            context.Database.ExecuteSqlRaw($"CREATE SCHEMA {schema};");
        }

        context.Database.Migrate();
    }

    private static string GetSchemaFromContext(SemanticoContext context)
    {
        // Access the protected DefaultSchema property through reflection
        var defaultSchemaProperty = typeof(SemanticoContext).GetProperty("DefaultSchema",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return defaultSchemaProperty?.GetValue(context) as string ?? "semantico";
    }
}

public class SemanticoConfiguration
{
    public string ConnectionStringName { get; set; } = nameof(SemanticoContext);

    /// <summary>
    /// Base URL of the Semantico admin UI (e.g., https://your-domain.com/semantico)
    /// Used for generating links in notifications.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Enables AI-powered features in the UI (documentation generation, smart alerts).
    /// Requires LLM configuration in appsettings.json.
    /// </summary>
    public bool UseAI { get; set; } = false;

    /// <summary>
    /// Controls database metadata loading behavior. Useful for large databases with hundreds of tables.
    /// </summary>
    public MetadataLoadingOptions MetadataLoading { get; set; } = new();

    /// <summary>
    /// Authorization configuration
    /// </summary>
    public AuthorizationOptions Authorization { get; set; } = new();

    /// <summary>
    /// Authentication configuration
    /// </summary>
    public AuthenticationOptions Authentication { get; set; } = new();

    public void AddSemanticoScheduler<T>() where T : class, ISemanticoScheduler
    {
        SemanticoScheduler = typeof(T);
    }

    public void AddEmailAdapter<T>() where T : class, IEmailAdapter
    {
        EmailAdapter = typeof(T);
    }

    public void AddAuthorizationProvider<T>() where T : class, ISemanticoAuthorizationProvider
    {
        Authorization.ProviderType = typeof(T);
    }

    public void AddAuthenticationProvider<T>() where T : class, ISemanticoAuthenticationProvider
    {
        Authentication.ProviderType = typeof(T);
    }

    internal Type? SemanticoScheduler { get; set; }

    internal Type? EmailAdapter { get; set; }

    internal void ValidateCore()
    {
        if (SemanticoScheduler == null)
        {
            throw new SemanticoException($"Implementation of ISemanticoScheduler is required.");
        }
    }
}

/// <summary>
/// Configuration options for database metadata loading.
/// Use these settings to control memory usage when working with large databases.
/// </summary>
public class MetadataLoadingOptions
{
    /// <summary>
    /// Enables metadata loading. Set to false to completely disable database structure exploration.
    /// Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum number of tables to load per data source. Set to 0 for unlimited.
    /// Recommended: 500 for large databases to prevent memory issues.
    /// Default: 0 (unlimited)
    /// </summary>
    public int MaxTables { get; set; } = 0;

    /// <summary>
    /// Maximum number of columns to load per table. Set to 0 for unlimited.
    /// Recommended: 200 for large databases.
    /// Default: 0 (unlimited)
    /// </summary>
    public int MaxColumnsPerTable { get; set; } = 0;

    /// <summary>
    /// If true, loads only table names without column details. Significantly reduces memory usage.
    /// Default: false
    /// </summary>
    public bool LoadTableNamesOnly { get; set; } = false;

    /// <summary>
    /// List of schema names to exclude from metadata loading (case-insensitive).
    /// Useful for excluding system schemas like 'information_schema', 'pg_catalog', 'sys'.
    /// Default: empty (load all schemas)
    /// </summary>
    public List<string> ExcludeSchemas { get; set; } = new();

    /// <summary>
    /// List of schema names to include (case-insensitive). If specified, only these schemas are loaded.
    /// Takes precedence over ExcludeSchemas.
    /// Default: empty (load all schemas)
    /// </summary>
    public List<string> IncludeSchemas { get; set; } = new();
}

/// <summary>
/// Configuration options for authorization.
/// </summary>
public class AuthorizationOptions
{
    /// <summary>
    /// Enable authorization checks. Default: false (backward compatible)
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Authorization provider type. If null, uses DefaultAuthorizationProvider.
    /// </summary>
    public Type? ProviderType { get; set; }

    /// <summary>
    /// Enable resource-level authorization (requires provider support).
    /// Default: false (use global read/write only)
    /// </summary>
    public bool EnableResourceLevelAuthorization { get; set; } = false;
}

/// <summary>
/// Configuration options for authentication.
/// </summary>
public class AuthenticationOptions
{
    /// <summary>
    /// Enable login form. Default: false (backward compatible)
    /// </summary>
    public bool EnableLoginForm { get; set; } = false;

    /// <summary>
    /// Authentication provider type. If null, uses DefaultAuthenticationProvider (which fails).
    /// </summary>
    public Type? ProviderType { get; set; }

    /// <summary>
    /// Path to redirect to after successful login.
    /// Default: "/" (root of the Semantico UI)
    /// </summary>
    public string LoginRedirectPath { get; set; } = "/";

    /// <summary>
    /// Path to the login page.
    /// Default: "/login"
    /// </summary>
    public string LoginPath { get; set; } = "/login";

    /// <summary>
    /// Cookie expiration in hours for normal login.
    /// Default: 24 hours
    /// </summary>
    public int CookieExpirationHours { get; set; } = 24;

    /// <summary>
    /// Enable "Remember Me" functionality on login form.
    /// Default: true
    /// </summary>
    public bool EnableRememberMe { get; set; } = true;

    /// <summary>
    /// Cookie expiration in days when "Remember Me" is checked.
    /// Default: 30 days
    /// </summary>
    public int RememberMeExpirationDays { get; set; } = 30;
}

/// <summary>
/// Builder for configuring Semantico services with a database provider.
/// </summary>
public class SemanticoBuilder
{
    public IServiceCollection Services { get; }
    public IConfiguration Configuration { get; }

    internal SemanticoBuilder(IServiceCollection services, IConfiguration configuration)
    {
        Services = services;
        Configuration = configuration;
    }
}