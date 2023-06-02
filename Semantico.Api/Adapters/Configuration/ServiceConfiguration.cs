using Semantico.Api.Adapters.Mail;
using Semantico.Api.Adapters.Mail.SendGrid;
using Semantico.Api.Adapters.Teams;
using SendGrid;

namespace Semantico.Api.Adapters.Configuration;

public static class ServiceConfiguration
{
    public static void AddAdapters(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient();
        var settings = configuration.GetSection(nameof(SendGridSettings));
        var sendGridSettings = settings.Get<SendGridSettings>()!;
        services.Configure<SendGridSettings>(settings);

        services.AddSingleton<ISendGridClient>(provider =>
        {
            var apiKey = sendGridSettings.Apikey;
            return new SendGridClient(apiKey);
        });

        services.AddTransient<IMailAdapter, SendGridAdapter>();
        services.AddTransient<ITeamsAdapter, TeamsService>();
    }
}