using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Semantico.Core.Adapters;
using Semantico.Core.Adapters.Jira;
using Semantico.Core.Adapters.Mail;
using Semantico.Core.Adapters.Slack;
using Semantico.Core.Adapters.Teams;
using Semantico.Core.Adapters.Webhook;
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
        services.AddSingleton<IAdapter, WebhookAdapter>();
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
        services.TryAddTransient<IAppSettingsService, AppSettingsService>();
        services.TryAddTransient<IQueryVersionService, QueryVersionService>();
        services.TryAddTransient<IQueryApprovalService, QueryApprovalService>();

        services.TryAddTransient(typeof(ISemanticoScheduler), configurationOptions.SemanticoScheduler!);

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
                    services.TryAddScoped<ISemanticoAuthenticationProvider, Authentication.Providers.HybridAuthenticationProvider>();
                }
                else
                {
                    services.TryAddScoped<ISemanticoAuthenticationProvider, Authentication.Providers.DatabaseAuthenticationProvider>();
                }
            }

            // Register database-backed authorization provider
            services.TryAddScoped<ISemanticoAuthorizationProvider, Authorization.Providers.DatabaseAuthorizationProvider>();
        }

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

        // Hydrate configuration singletons from database settings
        InitializeAppSettings(scope.ServiceProvider);
    }

    private static void InitializeAppSettings(IServiceProvider serviceProvider)
    {
        try
        {
            var settingsService = serviceProvider.GetRequiredService<IAppSettingsService>();
            var settings = settingsService.GetSettingsAsync().GetAwaiter().GetResult();

            // Hydrate SemanticoConfiguration
            var config = serviceProvider.GetRequiredService<SemanticoConfiguration>();
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

    private static string GetSchemaFromContext(SemanticoContext context)
    {
        // Access the protected DefaultSchema property through reflection
        var defaultSchemaProperty = typeof(SemanticoContext).GetProperty("DefaultSchema",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return defaultSchemaProperty?.GetValue(context) as string ?? "semantico";
    }
}