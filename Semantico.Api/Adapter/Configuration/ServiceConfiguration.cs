using Semantico.Api.Adapter.Mail;
using Semantico.Api.Adapter.Mail.SendGrid;
using Semantico.Api.Adapter.Teams;

namespace Semantico.Api.Adapter.Configuration;

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

