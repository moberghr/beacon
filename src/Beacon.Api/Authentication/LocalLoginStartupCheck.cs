using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Data;

namespace Beacon.Api.Authentication;

/// <summary>
/// Checks the local-login switch (<c>Beacon:UserManagement:LocalLogin</c>) when the host starts. Off, with single sign-on
/// off, no external login page (<see cref="BeaconApiOptions.ExternalLogin"/>) and no break-glass account, nobody could sign
/// in interactively: startup fails and says so. Off with break-glass accounts, after the host has started, off the startup
/// thread and best effort, each listed name that matches no enabled internal user exactly (as stored, case included) is
/// warned about by its position in the list, never the name (§1.11); a database that cannot be read is noted, never a
/// failure.
/// </summary>
internal sealed class LocalLoginStartupCheck(
    ILogger<LocalLoginStartupCheck> logger,
    BeaconConfiguration? configuration = null,
    IOptions<OidcAuthenticationOptions>? oidcOptions = null,
    BeaconApiOptions? apiOptions = null) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        var localLogin = configuration?.UserManagement.LocalLogin;
        if (localLogin == null || localLogin.Enabled)
        {
            return next;
        }

        if (!localLogin.IsOffered && oidcOptions?.Value.Enabled != true && apiOptions?.ExternalLogin == null)
        {
            throw new InvalidOperationException(
                $"{LocalLoginOptions.SectionName}:Enabled is false, but single sign-on (Beacon:Authentication:Oidc) is off, no " +
                "external login page is configured and no break-glass account is listed, so nobody could sign in. Turn single " +
                $"sign-on on, list an emergency account in {LocalLoginOptions.SectionName}:BreakGlassUserNames, or set Enabled to true.");
        }

        if (!localLogin.IsOffered)
        {
            return next;
        }

        return builder =>
        {
            var lifetime = builder.ApplicationServices.GetService<IHostApplicationLifetime>();
            lifetime?.ApplicationStarted.Register(
                () => _ = Task.Run(() => WarnAboutUnknownBreakGlassNamesAsync(builder.ApplicationServices, localLogin, lifetime.ApplicationStopping)));

            next(builder);
        };
    }

    internal async Task WarnAboutUnknownBreakGlassNamesAsync(IServiceProvider services, LocalLoginOptions localLogin, CancellationToken cancellationToken)
    {
        var contextFactory = services.GetService<IDbContextFactory<BeaconContext>>();
        if (contextFactory == null)
        {
            return;
        }

        try
        {
            var names = localLogin.BreakGlassUserNames
                .Select(x => x.Trim())
                .ToList();
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var stored = await context.Users
                .Where(x => x.IsInternalUser)
                .Where(x => x.IsEnabled)
                .Where(x => names.Contains(x.UserName))
                .Select(x => x.UserName)
                .ToListAsync(cancellationToken);

            for (var index = 0; index < names.Count; index++)
            {
                // The database may compare ignoring case; a break-glass name must match the stored name exactly.
                if (!stored.Contains(names[index], StringComparer.Ordinal))
                {
                    logger.LogWarning(
                        "{Section}:BreakGlassUserNames entry {Index} matches no enabled internal user (names are compared exactly, case included, with the stored user name): that account cannot sign in while local login is off",
                        LocalLoginOptions.SectionName,
                        index);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(
                "The break-glass accounts could not be checked against the stored users ({ExceptionType})",
                ex.GetType().Name);
        }
    }
}
