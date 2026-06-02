using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Beacon.Api.SignalR;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.SampleProject.Services;

namespace Beacon.SampleProject.Authentication;

/// <summary>
/// Authentication-related service registration helpers, formerly in Beacon.UI.ServiceExtensions.
/// </summary>
public static class AuthServiceExtensions
{
    /// <summary>
    /// Adds cookie authentication for Beacon login form.
    /// </summary>
    public static IServiceCollection AddBeaconCookieAuthentication(this IServiceCollection services, string basePath = "/")
    {
        services.AddHttpContextAccessor();

        // Register user context (always available)
        services.TryAddScoped<IBeaconUserContext, HttpContextUserContext>();

        // Register authentication service
        services.TryAddScoped<IBeaconAuthenticationService, BeaconAuthenticationService>();

        // Register the cookie scheme without changing the host app's default authentication scheme.
        services.AddAuthentication()
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.LoginPath = new PathString($"{basePath.TrimEnd('/')}/login");
                options.LogoutPath = new PathString($"{basePath.TrimEnd('/')}/logout");
                options.AccessDeniedPath = new PathString($"{basePath.TrimEnd('/')}/login");
                options.Cookie.Name = "Beacon.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = context =>
                {
                    if (IsApiRequest(context))
                    {
                        return WriteJsonStatusAsync(context, StatusCodes.Status401Unauthorized, "Unauthorized");
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    if (IsApiRequest(context))
                    {
                        return WriteJsonStatusAsync(context, StatusCodes.Status403Forbidden, "Forbidden");
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            });

        // Persist Data Protection keys to the database so cookies survive app restarts
        services.AddDataProtection()
            .SetApplicationName("Beacon")
            .PersistKeysToDbContext<BeaconContext>();

        return services;
    }

    public static IServiceCollection AddBeaconOidcAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("Beacon:Authentication:Oidc");
        var options = section.Get<OidcAuthenticationOptions>() ?? new OidcAuthenticationOptions();

        services.Configure<OidcAuthenticationOptions>(section);

        if (!options.Enabled)
        {
            return services;
        }

        if (string.IsNullOrWhiteSpace(options.Authority)
            || string.IsNullOrWhiteSpace(options.ClientId)
            || string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            throw new InvalidOperationException(
                "Beacon:Authentication:Oidc is Enabled but Authority, ClientId, or ClientSecret is missing.");
        }

        services.AddAuthentication()
            .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, oidc =>
            {
                oidc.Authority = options.Authority;
                oidc.ClientId = options.ClientId;
                oidc.ClientSecret = options.ClientSecret;
                oidc.CallbackPath = options.CallbackPath;
                oidc.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                oidc.SignOutScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                oidc.ResponseType = OpenIdConnectResponseType.Code;
                oidc.UsePkce = true;
                oidc.SaveTokens = false;
                oidc.GetClaimsFromUserInfoEndpoint = true;
                oidc.MapInboundClaims = false;

                oidc.Scope.Clear();
                foreach (var scope in options.Scopes)
                {
                    oidc.Scope.Add(scope);
                }

                oidc.Events = new OpenIdConnectEvents
                {
                    OnTokenValidated = OidcEventHandlers.HandleTokenValidatedAsync,
                    OnRemoteFailure = OidcEventHandlers.HandleRemoteFailureAsync
                };
            });

        return services;
    }

    /// <summary>
    /// Adds JWT authentication support for Beacon.
    /// </summary>
    public static IServiceCollection AddBeaconJwtAuthentication(
        this IServiceCollection services,
        Action<JwtAuthenticationOptions> configure)
    {
        var options = new JwtAuthenticationOptions();
        configure(options);

        services.AddSingleton(options);

        services.TryAddScoped<JwtExternalApiAuthenticationProvider>();

        if (!string.IsNullOrEmpty(options.ExternalLoginEndpoint))
        {
            services.AddScoped<IBeaconAuthenticationProvider>(sp =>
                sp.GetRequiredService<JwtExternalApiAuthenticationProvider>());
        }

        return services;
    }

    /// <summary>
    /// Registers host-level identity and SignalR plumbing that depends on the authenticated
    /// user: the claims transformer that materialises Beacon role claims after cookie/OIDC
    /// sign-in, the Hangfire → SignalR job-state bridge (per-user notifications), and the
    /// SignalR <c>IUserIdProvider</c> that maps connections to <c>BeaconUser.Id</c>.
    /// Kept here (§2.12) so <c>Program.cs</c> stays a thin composition root.
    /// </summary>
    public static IServiceCollection AddBeaconHostInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<HangfireSignalRJobFilter>();
        services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, SampleClaimsTransformation>();
        services.AddSingleton<IUserIdProvider, HubUserIdProvider>();
        return services;
    }

    public static IApplicationBuilder UseBeaconJwtBearerAuthentication(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetService<JwtAuthenticationOptions>();
        if (options?.EnableBearerAuthentication == true)
        {
            app.UseMiddleware<JwtBearerAuthMiddleware>(options);
        }

        return app;
    }

    private static bool IsApiRequest(Microsoft.AspNetCore.Authentication.RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/beacon/api") || path.StartsWithSegments("/beacon/mcp"))
        {
            return true;
        }

        var accept = context.Request.Headers.Accept.ToString();
        if (!string.IsNullOrEmpty(accept) && accept.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static async Task WriteJsonStatusAsync(
        Microsoft.AspNetCore.Authentication.RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> context,
        int statusCode,
        string title)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        var payload = $"{{\"type\":\"about:blank\",\"title\":\"{title}\",\"status\":{statusCode}}}";
        await context.Response.WriteAsync(payload, context.HttpContext.RequestAborted);
    }
}
