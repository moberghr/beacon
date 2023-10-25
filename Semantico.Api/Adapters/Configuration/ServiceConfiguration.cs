using Semantico.Api.Adapters.Jira;
using Semantico.Api.Adapters.Mail;
using Semantico.Api.Adapters.Mail.SendGrid;
using Semantico.Api.Adapters.Teams;
using Semantico.Api.Services;
using SendGrid;

namespace Semantico.Api.Adapters.Configuration;

public static class ServiceConfiguration
{
    public static void AddAdapters(this IServiceCollection services, IConfiguration configuration)
    {
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
        services.AddTransient<IJiraAdapter, JiraAdapter>();
    }
}