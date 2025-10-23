using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Semantico.Core.Adapters;
using Semantico.Core.Adapters.Jira;
using Semantico.Core.Adapters.Mail;
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
    public static IServiceCollection AddSemantico(this IServiceCollection services, IConfiguration configuration, Action<SemanticoConfiguration> semanticoConfiguration)
    {
        var configurationOptions = new SemanticoConfiguration();
        semanticoConfiguration(configurationOptions);
        configurationOptions.Validate();

        // Note: DbContext registration is now handled by the database provider-specific extension methods
        // (e.g., AddPostgreSqlSemantico or AddSqlServerSemantico)

        services.AddHttpClient();
        
        services.AddSingleton<IAdapter, TeamsAdapter>();
        if (configurationOptions.EmailAdapter != null)
        {
            services.TryAddSingleton(typeof(IEmailAdapter), configurationOptions.EmailAdapter);
            services.AddSingleton<IAdapter, EmailAdapter>();
        }
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
        services.TryAddTransient<IProjectService, ProjectService>();
        services.TryAddTransient<IQueryService, QueryService>();
        services.TryAddTransient<IQueryExecutionPreviewService, QueryExecutionPreviewService>();
        services.TryAddTransient<ISubscriptionService, SubscriptionService>();
        services.TryAddTransient<IRecipientService, RecipientService>();
        services.TryAddTransient<IStatisticsService, StatisticsService>();
        services.TryAddTransient<IMigrationService, MigrationService>();

        services.TryAddTransient(typeof(ISemanticoScheduler), configurationOptions.SemanticoScheduler!);

        return services;
    }

    public static void UseSemantico(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SemanticoContext>>();
        using var context = contextFactory.CreateDbContext();

        // Get the schema name from the context
        var schema = GetSchemaFromContext(context);

        // Ensure the schema exists before running migrations
        if (!string.IsNullOrEmpty(schema) && schema != "public")
        {
            context.Database.ExecuteSqlRaw($"CREATE SCHEMA IF NOT EXISTS \"{schema}\"");
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

    internal void Validate()
    {
        if (SemanticoScheduler == null)
        {
            throw new SemanticoException($"Implementation of ISemanticoScheduler is required.");
        }
    }
}