using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Semantico.Core.Adapters.Jira;
using Semantico.Core.Adapters.Mail;
using Semantico.Core.Adapters.Teams;
using Semantico.Core.Data;
using Semantico.Core.Services;
using Semantico.Core.Worker;
using Semantico.Core.Worker.Repositories;
using Semantico.Core.Worker.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Semantico.Core.Adapters;
using Semantico.Core.Models;

namespace Semantico.Core;

public static class ServiceConfiguration
{
    public static IServiceCollection AddSemantico(this IServiceCollection services, IConfiguration configuration, Action<SemanticoConfiguration> semanticoConfiguration)
    {
        var configurationOptions = new SemanticoConfiguration();
        semanticoConfiguration(configurationOptions);
        configurationOptions.Validate();

        services.AddDbContext<SemanticoContext>((options) =>
        {
            options.UseNpgsql(configuration.GetConnectionString(configurationOptions.ConnectionStringName))
                .UseSnakeCaseNamingConvention();
        });

        services.AddHttpClient();
        services.TryAddSingleton<IAdapter, TeamsAdapter>();
        if (configurationOptions.EmailSender != null)
        {
            services.TryAddSingleton(typeof(IEmailSender), configurationOptions.EmailSender);
            services.TryAddSingleton<IAdapter, EmailAdapter>();
        }
        services.TryAddSingleton<IAdapter, JiraAdapter>();
        services.TryAddSingleton<AdapterFactory>();

        services.TryAddTransient<IJobRepository, JobRepository>();
        services.TryAddTransient<IJobService, JobService>();
        services.TryAddTransient<INotificationService, NotificationService>();
        services.TryAddTransient<IProjectService, ProjectService>();
        services.TryAddTransient<IQueryService, QueryService>();
        services.TryAddTransient<ISubscriptionService, SubscriptionService>();
        services.TryAddTransient<IStatisticsService, StatisticsService>();

        services.TryAddTransient(typeof(ISemanticoScheduler), configurationOptions.SemanticoScheduler!);

        return services;
    }

    public static IApplicationBuilder UseSemantico(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SemanticoContext>();

        context.Database.Migrate();
        
        return app;
    }
}

public class SemanticoConfiguration
{
    public string ConnectionStringName { get; set; } = nameof(SemanticoContext);

    public void AddSemanticoScheduler<T>() where T : class, ISemanticoScheduler
    {
        SemanticoScheduler = typeof(T);
    }

    public void AddEmailSender<T>() where T : class, IEmailSender
    {
        SemanticoScheduler = typeof(T);
    }

    internal Type? SemanticoScheduler { get; set; }

    internal Type? EmailSender { get; set; }

    internal void Validate()
    {
        if (SemanticoScheduler == null)
        {
            throw new SemanticoException($"Implementation of ISemanticoScheduler is required.");
        }
    }
}