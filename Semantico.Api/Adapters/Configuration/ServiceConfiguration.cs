using Semantico.Api.Adapters.Mail;
using Semantico.Api.Adapters.Mail.SendGrid;
using Semantico.Api.Adapters.Teams;

namespace Semantico.Api.Adapters.Configuration;

public static class ServiceConfiguration
{
    public static void AddAdapters(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient();
        services.Configure<SendGridSettings>(configuration.GetSection(nameof(SendGridSettings)));
        services.AddTransient<IMailAdapter, SendGridService>();
        services.AddTransient<ITeamsAdapter, TeamsService>();
    }
}

