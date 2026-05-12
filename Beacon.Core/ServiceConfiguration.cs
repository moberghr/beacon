using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Beacon.Core.Adapters;
using Beacon.Core.Adapters.Jira;
using Beacon.Core.Adapters.Mail;
using Beacon.Core.Adapters.Slack;
using Beacon.Core.Adapters.Teams;
using Beacon.Core.Adapters.Webhook;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.Providers;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Embed;
using Beacon.Core.Services.Shared;
using Microsoft.Extensions.Options;
using Beacon.Core.Worker;
using Beacon.Core.Worker.Repositories;
using Beacon.Core.Worker.Services;

namespace Beacon.Core;

public static class ServiceConfiguration
{
    /// <summary>
    /// Adds core Beacon services (without UI components).
    /// Chain with .UsePostgreSql() or .UseSqlServer() to configure the database provider.
    /// </summary>
    public static BeaconBuilder AddBeaconServices(this IServiceCollection services, IConfiguration configuration, Action<BeaconConfiguration> beaconConfiguration)
    {
        var configurationOptions = new BeaconConfiguration();
        beaconConfiguration(configurationOptions);
        configurationOptions.ValidateCore();

        // Register configuration for access by adapters and other services
        services.AddSingleton(configurationOptions);

        services.AddHttpClient();
        services.AddMemoryCache();

        // Note: IBeaconUserContext and IBeaconAuthorizationProvider are registered by UI layer
        // Core only provides the interfaces and default implementations

        // Register authorization provider
        if (configurationOptions.Authorization.ProviderType != null)
        {
            services.TryAddScoped(
                typeof(IBeaconAuthorizationProvider),
                configurationOptions.Authorization.ProviderType);
        }
        else
        {
            // Default: allow all (backward compatible)
            services.TryAddScoped<IBeaconAuthorizationProvider, DefaultAuthorizationProvider>();
        }

        // Register authentication provider
        if (configurationOptions.Authentication.ProviderType != null)
        {
            services.TryAddScoped(
                typeof(IBeaconAuthenticationProvider),
                configurationOptions.Authentication.ProviderType);
        }
        else
        {
            // Default: authentication fails (requires explicit configuration)
            services.TryAddScoped<IBeaconAuthenticationProvider, DefaultAuthenticationProvider>();
        }

        // MediatR for CQRS pattern
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ServiceConfiguration).Assembly));

        // Encryption service for sensitive data (e.g., connection strings)
        var encryptionKey = configuration["Beacon:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(encryptionKey))
        {
            throw new InvalidOperationException(
                "Beacon:EncryptionKey must be configured. " +
                "Generate a secure key with: openssl rand -base64 32" +
                Environment.NewLine +
                "Then add to appsettings.json: { \"Beacon\": { \"EncryptionKey\": \"your-generated-key\" } }");
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
        services.AddSingleton<IAdapter, WebhookAdapter>();
        services.TryAddSingleton<AdapterFactory>();

        // Shared services (for use by Query and Migration features)
        services.TryAddTransient<QueryExecutionOrchestrator>();
        services.TryAddTransient<ParameterResolver>();
        services.TryAddTransient<SchedulingService>();

        // Data source providers — DatabaseProvider is always registered (connector projects register connection factories and engine types)
        // Non-database providers (CloudWatch, Databricks, BigQuery) are registered by their connector projects
        services.AddTransient<Services.Providers.IDataSourceProvider, Services.Providers.DatabaseProvider>();
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
        services.TryAddTransient<IControlTowerService, ControlTowerService>();
        services.TryAddTransient<IDashboardService, DashboardService>();
        services.TryAddTransient<IMigrationService, MigrationService>();
        services.TryAddTransient<IDatabaseMetadataService, DatabaseMetadataService>();
        services.TryAddTransient<IAnomalyDetectionService, AnomalyDetectionService>();
        services.TryAddTransient<IDataQualitySqlGenerator, DataQualitySqlGenerator>();
        services.TryAddTransient<IDataQualityEvaluationService, DataQualityEvaluationService>();
        services.TryAddTransient<IManualQueryExecutionLogger, ManualQueryExecutionLogger>();
        services.TryAddTransient<IAppSettingsService, AppSettingsService>();
        services.TryAddTransient<IQueryVersionService, QueryVersionService>();
        services.TryAddTransient<IQueryApprovalService, QueryApprovalService>();

        services.TryAddTransient(typeof(IBeaconScheduler), configurationOptions.BeaconScheduler!);

        // MCP settings provider (cached reads for MCP tool configuration)
        services.TryAddTransient<IMcpSettingsProvider, McpSettingsProvider>();

        // API key service (always registered — used for stateless API authentication)
        services.TryAddTransient<Services.Security.IApiKeyService, Services.Security.ApiKeyService>();

        // Query guardrail service (always registered — enforces read-only access and PII detection)
        services.TryAddTransient<Services.Security.IQueryGuardrailService, Services.Security.QueryGuardrailService>();

        // Rate limiter (singleton so the in-memory sliding windows are shared across requests)
        services.TryAddSingleton<Services.Security.RateLimiter>();

        // User Management services (opt-in feature)
        if (configurationOptions.UserManagement.Enabled)
        {
            services.TryAddSingleton<Services.Security.IPasswordHasher, Services.Security.PasswordHasher>();
            services.TryAddTransient<IRoleService, RoleService>();
            services.TryAddTransient<IUserManagementService, UserManagementService>();

            // Register database-backed authentication provider
            if (configurationOptions.UserManagement.AllowInternalUsers)
            {
                // If JWT is also configured, use hybrid provider
                if (configurationOptions.Authentication.Jwt?.ExternalLoginEndpoint != null)
                {
                    services.TryAddScoped<IBeaconAuthenticationProvider, Authentication.Providers.HybridAuthenticationProvider>();
                }
                else
                {
                    services.TryAddScoped<IBeaconAuthenticationProvider, Authentication.Providers.DatabaseAuthenticationProvider>();
                }
            }

            // Register database-backed authorization provider
            services.TryAddScoped<IBeaconAuthorizationProvider, Authorization.Providers.DatabaseAuthorizationProvider>();
        }

        // Embed token service (HS256 mint/validate for embeddable Beacon integrations)
        services.Configure<EmbedTokenOptions>(configuration.GetSection("Beacon:EmbedToken"));
        services.AddSingleton<IValidateOptions<EmbedTokenOptions>, EmbedTokenOptionsValidator>();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IEmbedTokenService, EmbedTokenService>();

        return new BeaconBuilder(services, configuration);
    }

    public static void UseBeacon(IServiceProvider serviceProvider, bool createSchema = false)
    {
        using var scope = serviceProvider.CreateScope();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BeaconContext>>();
        using var context = contextFactory.CreateDbContext();

        // Get the schema name from the context
        var schema = GetSchemaFromContext(context);

        // Ensure the schema exists before running migrations
        if (createSchema)
        {
            context.Database.ExecuteSqlRaw($"CREATE SCHEMA {schema};");
        }

        context.Database.Migrate();

        // Hydrate configuration singletons from database settings
        InitializeAppSettings(scope.ServiceProvider);
    }

    private static void InitializeAppSettings(IServiceProvider serviceProvider)
    {
        try
        {
            var settingsService = serviceProvider.GetRequiredService<IAppSettingsService>();
            var settings = settingsService.GetSettingsAsync().GetAwaiter().GetResult();

            // Hydrate BeaconConfiguration
            var config = serviceProvider.GetRequiredService<BeaconConfiguration>();
            if (settings.BaseUrl != null) config.BaseUrl = settings.BaseUrl;

            // Hydrate LLM configuration if updater is available
            var llmUpdater = serviceProvider.GetService<ILlmConfigurationUpdater>();
            if (llmUpdater != null && settings.LlmProvider.HasValue)
            {
                llmUpdater.UpdateConfiguration(settings);
            }
        }
        catch
        {
            // Settings table may not exist yet on first run before migration — continue with defaults
        }
    }

    private static string GetSchemaFromContext(BeaconContext context)
    {
        // Access the protected DefaultSchema property through reflection
        var defaultSchemaProperty = typeof(BeaconContext).GetProperty("DefaultSchema",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return defaultSchemaProperty?.GetValue(context) as string ?? "beacon";
    }
}