using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Semantico.Core.Adapters;
using Semantico.Core.Adapters.Jira;
using Semantico.Core.Adapters.Mail;
using Semantico.Core.Adapters.Slack;
using Semantico.Core.Adapters.Teams;
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

    public void AddSemanticoScheduler<T>() where T : class, ISemanticoScheduler
    {
        SemanticoScheduler = typeof(T);
    }

    public void AddEmailAdapter<T>() where T : class, IEmailAdapter
    {
        EmailAdapter = typeof(T);
    }

    public void AddAuthorizationProvider<T>() where T : class
    {
        AuthorizationProvider = typeof(T);
    }

    internal Type? SemanticoScheduler { get; set; }

    internal Type? EmailAdapter { get; set; }

    public Type? AuthorizationProvider { get; set; }

    internal void ValidateCore()
    {
        if (SemanticoScheduler == null)
        {
            throw new SemanticoException($"Implementation of ISemanticoScheduler is required.");
        }
    }
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