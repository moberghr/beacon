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

        services.AddDbContextFactory<SemanticoContext>((options) =>
        {
            options.UseNpgsql(configuration.GetConnectionString(configurationOptions.ConnectionStringName),
                builder => builder.MigrationsHistoryTable("__EFMigrationsHistory", "semantico"))
                .UseSnakeCaseNamingConvention();
        });

        services.AddHttpClient();
        
        services.AddSingleton<IAdapter, TeamsAdapter>();
        if (configurationOptions.EmailAdapter != null)
        {
            services.TryAddSingleton(typeof(IEmailAdapter), configurationOptions.EmailAdapter);
            services.AddSingleton<IAdapter, EmailAdapter>();
        }
        services.AddSingleton<IAdapter, JiraAdapter>();
        services.TryAddSingleton<AdapterFactory>();

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
        var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SemanticoContext>();

        context.Database.Migrate();
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

    internal Type? SemanticoScheduler { get; set; }

    internal Type? EmailAdapter { get; set; }

    internal void Validate()
    {
        if (SemanticoScheduler == null)
        {
            throw new SemanticoException($"Implementation of ISemanticoScheduler is required.");
        }
    }
}