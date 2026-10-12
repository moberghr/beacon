using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;

namespace Beacon.Api.Authentication;

/// <summary>
/// The server side of a <c>Beacon.Auth</c> cookie session. At sign-in the ticket records when the session started and
/// the user's security stamp, and the session carries the roles the user holds then (an inactive user gets no session:
/// <see cref="WasSignInRefused"/>). Every request checks the absolute lifetime
/// (<see cref="SessionLifetimeOptions.AbsoluteLifetimeHours"/>, however often the cookie is renewed) and caps the
/// ticket's expiry to it; a cookie renewed on a request (by a check, or by the scheme's sliding expiration) expires no
/// later either, and a scheme without sliding expiration keeps its expiry. With Beacon user management on, at most every
/// <see cref="SessionLifetimeOptions.RevalidateMinutes"/> the session is checked against the user store: a user who is
/// missing, disabled or archived, or whose security stamp changed since the sign-in, is signed out; the session's role
/// claims are replaced by the roles the user holds now (and those the identity provider mapped at sign-in, while that
/// mapping is on and for <c>IdpRoles:ClaimLifetimeMinutes</c> after the sign-in). A realtime hub request is checked every
/// time and reports an expiry of the next check (<see cref="SessionLifetimeOptions.RealtimeConnectionLifetime"/>), so the
/// hub closes the connection then and the client reconnects through a fresh check; that expiry is never written to the
/// cookie. A host without Beacon user management keeps its own principals: only the lifetime applies.
/// Runs inside the cookie handler, so <see cref="BeaconCookieAuthMiddleware"/> and every other cookie authentication
/// of the request see its outcome (§1.9 order unchanged). Ids only in logs (§1.11).
/// </summary>
internal static class BeaconSessionEvents
{
    // Ticket properties (protected with the ticket, never sent as claims).
    internal const string SignedInAtItem = ".beacon.session.signed_in";
    internal const string ValidatedAtItem = ".beacon.session.validated";
    internal const string StampItem = ".beacon.session.stamp";

    // Set on the request whose sign-in was refused because the user is disabled or archived.
    private const string SignInRefusedItem = "Beacon.Session.SignInRefused";

    private const string LoggerName = "Beacon.Api.Authentication.BeaconSessionEvents";

    /// <summary>
    /// Records the session's start (kept when a session is signed in again with its own properties), the time it was
    /// last checked, and the user's security stamp; caps the expiry at the absolute lifetime. With Beacon user management
    /// on, the one read of the user that supplies the stamp also supplies the roles the session carries, and a user who is
    /// disabled or archived by then gets no session: the cookie is issued already expired and without an identity, and
    /// <see cref="WasSignInRefused"/> tells the caller.
    /// </summary>
    public static async Task SigningInAsync(CookieSigningInContext context)
    {
        var services = context.HttpContext.RequestServices;
        var lifetime = LifetimeOptions(services);
        var now = (context.Options.TimeProvider ?? TimeProvider.System).GetUtcNow();
        var properties = context.Properties;

        var signedInAt = ReadTime(properties, SignedInAtItem) ?? now;
        properties.Items[SignedInAtItem] = FormatTime(signedInAt);
        properties.Items[ValidatedAtItem] = FormatTime(now);
        properties.Items.Remove(StampItem);

        var store = UserStore(services);
        var externalId = NameIdentifierOf(context.Principal);
        if (store != null && externalId != null)
        {
            var user = await store.FindAsync(externalId, context.HttpContext.RequestAborted);
            if (user is { IsActive: false })
            {
                Logger(services)?.LogInformation(
                    ProjectAccessEvents.SessionEnded,
                    "Session ended: {Reason} (user {UserId})",
                    "user_disabled_or_archived_at_sign_in",
                    user.Id);

                context.HttpContext.Items[SignInRefusedItem] = true;
                context.Principal = new ClaimsPrincipal(new ClaimsIdentity());
                properties.IsPersistent = false;
                properties.ExpiresUtc = now;

                return;
            }

            if (user != null)
            {
                if (!string.IsNullOrEmpty(user.SecurityStamp))
                {
                    properties.Items[StampItem] = user.SecurityStamp;
                }

                var roleMapping = services.GetService<IOptions<RoleMappingOptions>>()?.Value;
                var principal = WithStoredRoles(context.Principal!, user, roleMapping, now) ?? context.Principal!;
                context.Principal = WithStampClaim(principal, user.SecurityStamp ?? string.Empty);
            }
        }

        var endsAt = signedInAt + lifetime.AbsoluteLifetime;
        if (properties.ExpiresUtc == null || properties.ExpiresUtc > endsAt)
        {
            properties.ExpiresUtc = endsAt;
        }
    }

    /// <summary>
    /// True when this request signed a user in who was disabled or archived by then (<see cref="SigningInAsync"/>): the
    /// cookie it issued carries no session, so the caller answers as for any refused sign-in.
    /// </summary>
    public static bool WasSignInRefused(HttpContext httpContext)
    {
        return httpContext.Items.ContainsKey(SignInRefusedItem);
    }

    /// <summary>Checks the session as the class describes; a refused session is rejected and its cookie deleted.</summary>
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var lifetime = LifetimeOptions(services);
        var now = (context.Options.TimeProvider ?? TimeProvider.System).GetUtcNow();
        var properties = context.Properties;
        var hubRequest = IsHubRequest(context.HttpContext);
        var renew = IsSlidingRenewal(context.Options, properties, now);

        var signedInAt = ReadTime(properties, SignedInAtItem);
        if (signedInAt == null)
        {
            // A session signed in before sessions recorded their start: its lifetime counts from its last renewal.
            signedInAt = properties.IssuedUtc ?? now;
            properties.Items[SignedInAtItem] = FormatTime(signedInAt.Value);
            renew = true;
        }

        var endsAt = signedInAt.Value + lifetime.AbsoluteLifetime;
        if (endsAt <= now)
        {
            await RejectAsync(context, "absolute_lifetime", null);
            return;
        }

        // What this request reports as the session's expiry (a SignalR connection closes then) never passes the
        // absolute lifetime.
        if (properties.ExpiresUtc == null || properties.ExpiresUtc > endsAt)
        {
            properties.ExpiresUtc = endsAt;
        }

        var store = UserStore(services);
        if (store != null && (hubRequest || IsRevalidationDue(properties, lifetime, now)))
        {
            var externalId = NameIdentifierOf(context.Principal);
            var user = externalId == null
                ? null
                : await store.FindAsync(externalId, context.HttpContext.RequestAborted);
            if (user == null)
            {
                await RejectMissingAsync(context, externalId);
                return;
            }

            if (!user.IsActive)
            {
                await RejectAsync(context, "user_disabled_or_archived", user.Id);
                return;
            }

            if (!string.Equals(StampOf(properties), user.SecurityStamp ?? string.Empty, StringComparison.Ordinal))
            {
                await RejectAsync(context, "security_stamp_changed", user.Id);
                return;
            }

            var roleMapping = services.GetService<IOptions<RoleMappingOptions>>()?.Value;
            var replaced = WithStoredRoles(context.Principal!, user, roleMapping, now);
            if (replaced != null)
            {
                context.ReplacePrincipal(replaced);
                Logger(services)?.LogInformation("Session roles of user {UserId} replaced by the stored roles", user.Id);
            }

            properties.Items[ValidatedAtItem] = FormatTime(now);
            renew = renew || lifetime.RevalidateMinutes > 0 || replaced != null;
        }

        if (hubRequest)
        {
            // A hub connection outlives the request that opened it: it is closed when the session's next check is due,
            // and the client reconnects through a new check. Reported for this request only: a hub request never renews
            // the cookie, so the shorter expiry (and this check) is not written to it.
            var recheckAt = now + lifetime.RealtimeConnectionLifetime;
            if (store != null && properties.ExpiresUtc > recheckAt)
            {
                properties.ExpiresUtc = recheckAt;
            }

            return;
        }

        if (renew)
        {
            // A renewed cookie is issued for (expiry - issued) from now. Renewing to record the check must not extend a
            // session the cookie scheme does not slide; one that slides keeps its window, but never past the absolute
            // lifetime (this renewal replaces the scheme's own sliding one, which would issue the whole window again).
            properties.IssuedUtc = context.Options.SlidingExpiration
                ? Later(properties.IssuedUtc, properties.ExpiresUtc - (endsAt - now))
                : now;
            context.ShouldRenew = true;
        }
    }

    /// <summary>
    /// Signs the request's cookie session in again with its own claims and properties, so it carries the user's
    /// current security stamp: after the user changed their own password, which signs out their other sessions. Does
    /// nothing for a request without a cookie session.
    /// </summary>
    public static async Task RefreshSignInAsync(HttpContext httpContext)
    {
        var handlers = httpContext.RequestServices.GetService<IAuthenticationHandlerProvider>();
        var handler = handlers == null
            ? null
            : await handlers.GetHandlerAsync(httpContext, CookieAuthenticationDefaults.AuthenticationScheme);
        if (handler == null)
        {
            return;
        }

        var result = await handler.AuthenticateAsync();
        if (!result.Succeeded)
        {
            return;
        }

        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, result.Principal, result.Properties);
    }

    // The roles a fresh sign-in would carry, as role claims of the same kinds the session holds; null when they already
    // are the session's. The identity provider's mapped roles count while the mapping is on and the sign-in that mapped
    // them is at most IdpRoles:ClaimLifetimeMinutes old, as the permission accessor honours them.
    internal static ClaimsPrincipal? WithStoredRoles(
        ClaimsPrincipal principal,
        SessionUser user,
        RoleMappingOptions? roleMapping,
        DateTimeOffset now)
    {
        var expected = user.RoleNames.ToHashSet(StringComparer.Ordinal);
        if (roleMapping is { IdpRoles.Enabled: true } && IdpRolesAreCurrent(principal, roleMapping, now))
        {
            var mapped = principal
                .FindAll(BeaconClaims.IdpPermission)
                .Where(x => x.Issuer == BeaconClaims.Issuer)
                .Select(x => BeaconPermissions.Find(x.Value))
                .OfType<string>()
                .Select(roleMapping.RoleNameFor);
            expected.UnionWith(mapped);
        }

        var held = principal
            .FindAll(ClaimTypes.Role)
            .Select(x => x.Value)
            .ToHashSet(StringComparer.Ordinal);
        if (held.SetEquals(expected))
        {
            return null;
        }

        var addsBeaconRole = principal.HasClaim(x => x.Type == BeaconClaims.Role);
        var replaced = principal.Clone();
        foreach (var identity in replaced.Identities)
        {
            var roleClaims = identity.Claims
                .Where(x => x.Type == ClaimTypes.Role || x.Type == BeaconClaims.Role || x.Type == identity.RoleClaimType)
                .ToList();
            foreach (var claim in roleClaims)
            {
                identity.RemoveClaim(claim);
            }
        }

        var primary = replaced.Identities.First();
        foreach (var role in expected.Order(StringComparer.Ordinal))
        {
            primary.AddClaim(new Claim(ClaimTypes.Role, role));
            if (addsBeaconRole)
            {
                primary.AddClaim(new Claim(BeaconClaims.Role, role));
            }

            if (!string.Equals(primary.RoleClaimType, ClaimTypes.Role, StringComparison.Ordinal))
            {
                primary.AddClaim(new Claim(primary.RoleClaimType, role));
            }
        }

        return replaced;
    }

    // The principal with exactly one session stamp claim (BeaconClaims.SessionStamp), the one given; a copy, so the
    // principal the caller signed in with is left as it was.
    internal static ClaimsPrincipal WithStampClaim(ClaimsPrincipal principal, string stamp)
    {
        var stamped = principal.Clone();
        foreach (var identity in stamped.Identities)
        {
            var stale = identity.Claims
                .Where(x => x.Type == BeaconClaims.SessionStamp)
                .ToList();
            foreach (var claim in stale)
            {
                identity.RemoveClaim(claim);
            }
        }

        stamped.Identities.First().AddClaim(new Claim(BeaconClaims.SessionStamp, stamp, ClaimValueTypes.String, BeaconClaims.Issuer));

        return stamped;
    }

    private static bool IdpRolesAreCurrent(ClaimsPrincipal principal, RoleMappingOptions roleMapping, DateTimeOffset now)
    {
        var issuedAt = principal
            .FindAll(BeaconClaims.IdpPermissionsIssuedAt)
            .Where(x => x.Issuer == BeaconClaims.Issuer)
            .Select(x => long.TryParse(x.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? (long?)seconds : null)
            .FirstOrDefault();
        var age = now.ToUnixTimeSeconds() - issuedAt;

        return age != null && age <= TimeSpan.FromMinutes(roleMapping.IdpRoles.ClaimLifetimeMinutes).TotalSeconds;
    }

    // A session naming no stored user: from a host or a sign-in Beacon does not know, or a user deleted outright. Logged
    // at Warning with a fingerprint of the id it presented, never the id.
    private static async Task RejectMissingAsync(CookieValidatePrincipalContext context, string? externalId)
    {
        Logger(context.HttpContext.RequestServices)?.LogWarning(
            ProjectAccessEvents.SessionEnded,
            "Session ended: {Reason} (subject {SubjectHash})",
            "user_missing",
            SubjectFingerprint.Of(externalId));

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(context.Scheme.Name);
    }

    // The realtime hub's requests (negotiate and connect), by their endpoint or, before routing, by the hub's route.
    private static bool IsHubRequest(HttpContext httpContext)
    {
        return httpContext.GetEndpoint()?.Metadata.GetMetadata<HubMetadata>() != null
            || httpContext.Request.Path.StartsWithSegments(Endpoints.BeaconApiEndpoints.HubPath);
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context, string reason, int? userId)
    {
        Logger(context.HttpContext.RequestServices)?.LogInformation(
            ProjectAccessEvents.SessionEnded,
            "Session ended: {Reason} (user {UserId})",
            reason,
            userId);

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(context.Scheme.Name);
    }

    // The cookie scheme's own sliding renewal of this request, decided before this event from the ticket as stored: past
    // half its window, the scheme issues the cookie for the whole window again from now (CookieAuthenticationHandler).
    private static bool IsSlidingRenewal(CookieAuthenticationOptions options, AuthenticationProperties properties, DateTimeOffset now)
    {
        var issuedAt = properties.IssuedUtc;
        var expiresAt = properties.ExpiresUtc;

        return options.SlidingExpiration
            && (properties.AllowRefresh ?? true)
            && issuedAt != null
            && expiresAt != null
            && expiresAt.Value - now < now - issuedAt.Value;
    }

    private static DateTimeOffset? Later(DateTimeOffset? first, DateTimeOffset? second)
    {
        return first == null || second > first ? second : first;
    }

    private static bool IsRevalidationDue(AuthenticationProperties properties, SessionLifetimeOptions lifetime, DateTimeOffset now)
    {
        var validatedAt = ReadTime(properties, ValidatedAtItem);

        return lifetime.RevalidateMinutes == 0
            || validatedAt == null
            || validatedAt > now
            || now - validatedAt.Value >= lifetime.RevalidateInterval;
    }

    // The stamp the session was signed in with; a session signed in while the user had none (or before stamps) has "".
    private static string StampOf(AuthenticationProperties properties)
    {
        return properties.Items.TryGetValue(StampItem, out var stamp) ? stamp ?? string.Empty : string.Empty;
    }

    private static string? NameIdentifierOf(ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return string.IsNullOrEmpty(value) ? null : value;
    }

    // The user store, only while Beacon user management is on; a host without it keeps its own principals.
    private static IUserSessionStore? UserStore(IServiceProvider services)
    {
        var configuration = services.GetService<BeaconConfiguration>();

        return configuration?.UserManagement.Enabled == true
            ? services.GetService<IUserSessionStore>()
            : null;
    }

    private static SessionLifetimeOptions LifetimeOptions(IServiceProvider services)
    {
        return services.GetService<IOptions<SessionLifetimeOptions>>()?.Value ?? new SessionLifetimeOptions();
    }

    private static ILogger? Logger(IServiceProvider services)
    {
        return services.GetService<ILoggerFactory>()?.CreateLogger(LoggerName);
    }

    private static DateTimeOffset? ReadTime(AuthenticationProperties properties, string key)
    {
        return properties.Items.TryGetValue(key, out var value)
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
    }

    private static string FormatTime(DateTimeOffset value)
    {
        return value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    }
}
