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

            services.AddSingleton<ISendGridClient>(provider =>
            {
                var apiKey = sendGridSettings.Apikey;
                return new SendGridClient(apiKey);
            });

            services.AddHttpClient();
            services.AddSingleton<ITeamsAdapter, TeamsAdapter>();
            services.AddSingleton<IMailAdapter, SendGridAdapter>();
            services.AddSingleton<IJiraAdapter, JiraAdapter>();

            services.AddTransient<IJobRepository, JobRepository>();
            services.AddTransient<IJobService, JobService>();
            services.AddTransient<INotificationService, NotificationService>();
            services.AddTransient<IProjectService, ProjectService>();
            services.AddTransient<IQueryService, QueryService>();
            services.AddTransient<ISubscriptionService, SubscriptionService>();


            services.AddTransient<ISemanticoScheduler, TSemanticoScheduler>();

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
