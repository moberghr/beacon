using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Semantico.Core.Adapters.Jira;
using Semantico.Core.Adapters.Mail.SendGrid;
using Semantico.Core.Adapters.Mail;
using Semantico.Core.Adapters.Teams;
using Semantico.Core.Data;
using Semantico.Core.Services;
using Semantico.Core.Worker;
using Semantico.Core.Worker.Repositories;
using Semantico.Core.Worker.Services;
using SendGrid;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Semantico.Core.Adapters;

namespace Semantico.Core
{
    public static class ServiceConfiguration
    {
        public static IServiceCollection AddSemantico<TSemanticoScheduler>(this IServiceCollection services, IConfiguration configuration)
            where TSemanticoScheduler : class, ISemanticoScheduler
        {
            services.AddDbContext<SemanticoContext>((options) =>
            {
                options.UseNpgsql(configuration.GetConnectionString(nameof(SemanticoContext)))
                    .UseSnakeCaseNamingConvention();
            });

            var settings = configuration.GetSection(nameof(SendGridSettings));
            var sendGridSettings = settings.Get<SendGridSettings>()!;
            services.Configure<SendGridSettings>(settings);

            services.TryAddSingleton<ISendGridClient>(provider =>
            {
                var apiKey = sendGridSettings.Apikey;
                return new SendGridClient(apiKey);
            });

            services.AddHttpClient();
            services.TryAddSingleton<IAdapter, TeamsAdapter>();
            services.TryAddSingleton<IAdapter, SendGridAdapter>();
            services.TryAddSingleton<IAdapter, JiraAdapter>();
            services.TryAddSingleton<AdapterFactory>();

            services.TryAddTransient<IJobRepository, JobRepository>();
            services.TryAddTransient<IJobService, JobService>();
            services.TryAddTransient<INotificationService, NotificationService>();
            services.TryAddTransient<IProjectService, ProjectService>();
            services.TryAddTransient<IQueryService, QueryService>();
            services.TryAddTransient<ISubscriptionService, SubscriptionService>();

            services.TryAddTransient<ISemanticoScheduler, TSemanticoScheduler>();

            return services;
        }

        public static IApplicationBuilder UseSemantico(this WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<SemanticoContext>().Database.EnsureCreated();

            return app;
        }
    }
}
