using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Semantico.Core.Adapters.Jira;
using Semantico.Core.Adapters.Mail;
using Semantico.Core.Adapters.Mail.SendGrid;
using Semantico.Core.Adapters.Teams;
using Semantico.Core.Services;
using SendGrid;

namespace Semantico.Core.Adapters.Configuration;

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
        services.AddSingleton<IJiraAdapter, JiraAdapter>();
    }
}