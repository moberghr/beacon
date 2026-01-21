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
    internal static IServiceCollection AddSemanticoCore(this IServiceCollection services, IConfiguration configuration, Action<SemanticoConfiguration> semanticoConfiguration)
    {
        var configurationOptions = new SemanticoConfiguration();
        semanticoConfiguration(configurationOptions);
        configurationOptions.Validate();

        // Register configuration for access by adapters and other services
        services.AddSingleton(configurationOptions);

        // Note: DbContext registration is now handled by the database provider-specific extension methods
        // (e.g., AddPostgreSqlSemantico or AddSqlServerSemantico)

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

        // AI/LLM services
        RegisterAiServices(services, configuration);

        services.TryAddTransient(typeof(ISemanticoScheduler), configurationOptions.SemanticoScheduler!);

        return services;
    }

    private static void RegisterAiServices(IServiceCollection services, IConfiguration configuration)
    {
        // Load LLM configuration from appsettings
        var llmConfig = configuration.GetSection("Semantico:LLM").Get<Models.Configuration.LlmConfiguration>();

        if (llmConfig == null)
        {
            // Register null/no-op implementations when AI is not configured
            // This allows services like JobService to have their dependencies satisfied
            services.TryAddScoped<Services.Ai.AiActor.IAiActorService, Services.Ai.AiActor.NullAiActorService>();
            return;
        }

        services.AddSingleton(llmConfig);

        // LLM Provider Factory
        services.AddSingleton<Services.LlmProviders.LlmProviderFactory>();

        // LLM Provider (singleton for the application)
        services.AddSingleton<Services.LlmProviders.ILlmProvider>(sp =>
        {
            var factory = sp.GetRequiredService<Services.LlmProviders.LlmProviderFactory>();
            var provider = factory.CreateProvider();
            return provider;
        });

        // Request queue for rate limiting
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<Models.Configuration.LlmConfiguration>();
            return new Services.LlmProviders.LlmRequestQueue(
                config.Limits.MaxConcurrentRequests);
        });

        // AI services
        services.TryAddScoped<Services.Ai.IAiDocumentationService, Services.Ai.AiDocumentationService>();
        services.TryAddScoped<Services.Ai.IAiAlertGenerationService, Services.Ai.AiAlertGenerationService>();

        // Documentation Agent services (new agent-based approach)
        services.TryAddTransient<Services.Ai.DocumentationAgent.DocumentationAgentTools>();
        services.TryAddTransient<Services.Ai.DocumentationAgent.IDocumentationAgentService, Services.Ai.DocumentationAgent.DocumentationAgentService>();

        // AI Actor services (autonomous monitoring)
        services.TryAddScoped<Services.Ai.AiActor.IAiActorService, Services.Ai.AiActor.AiActorService>();
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

    /// <summary>
    /// Configure PostgreSQL as the database provider.
    /// </summary>
    public void UsePostgreSql(string connectionString, string schema = "semantico")
    {
        DatabaseProvider = DatabaseProviderType.PostgreSql;
        ConnectionString = connectionString;
        Schema = schema;
    }

    /// <summary>
    /// Configure SQL Server as the database provider.
    /// </summary>
    public void UseSqlServer(string connectionString, string schema = "semantico")
    {
        DatabaseProvider = DatabaseProviderType.SqlServer;
        ConnectionString = connectionString;
        Schema = schema;
    }

    internal Type? SemanticoScheduler { get; set; }

    internal Type? EmailAdapter { get; set; }

    public Type? AuthorizationProvider { get; set; }

    internal DatabaseProviderType? DatabaseProvider { get; set; }

    internal string? ConnectionString { get; set; }

    internal string Schema { get; set; } = "semantico";

    internal void Validate()
    {
        if (SemanticoScheduler == null)
        {
            throw new SemanticoException($"Implementation of ISemanticoScheduler is required.");
        }

        if (DatabaseProvider == null)
        {
            throw new SemanticoException($"Database provider must be configured. Call UsePostgreSql() or UseSqlServer() on the configuration options.");
        }

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new SemanticoException($"Connection string must be provided when configuring the database provider.");
        }
    }
}

internal enum DatabaseProviderType
{
    PostgreSql,
    SqlServer
}