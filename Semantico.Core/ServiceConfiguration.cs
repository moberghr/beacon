using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Semantico.Core.Adapters.Configuration;
using Semantico.Core.Data;
using Semantico.Core.Services;
using Semantico.Core.Worker;
using Semantico.Core.Worker.Repositories;
using Semantico.Core.Worker.Services;

namespace Semantico.Core
{
    public static class ServiceConfiguration
    {
        public static IServiceCollection AddSemanticoCore<TSemanticoScheduler>(this IServiceCollection services, IConfiguration configuration)
            where TSemanticoScheduler : class, ISemanticoScheduler
        {
            services.AddDbContext<SemanticoContext>((options) =>
            {
                options.UseNpgsql(
                        configuration.GetConnectionString(nameof(SemanticoContext)),
                        x => x.MigrationsHistoryTable("__SemanticoMigrationsHistory", "semantico"))
                    .UseSnakeCaseNamingConvention();
            });

            services.AddAdapters(configuration);

            services.AddTransient<IJobRepository, JobRepository>();
            services.AddTransient<IJobService, JobService>();
            services.AddTransient<INotificationService, NotificationService>();
            services.AddTransient<IAccountService, AccountService>();
            services.AddTransient<IProjectService, ProjectService>();
            services.AddTransient<IQueryService, QueryService>();
            services.AddTransient<ISubscriptionService, SubscriptionService>();


            services.AddSingleton<ISemanticoScheduler, TSemanticoScheduler>();

            return services;
        }
    }
}
