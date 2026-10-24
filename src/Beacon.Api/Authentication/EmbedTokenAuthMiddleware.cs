using Beacon.Core.SecurityAudit;
using Beacon.Core.Services.Embed;
using Microsoft.AspNetCore.Authentication;

namespace Beacon.Api.Authentication;

/// <summary>
/// What the embed middleware verified for an embed request: the grant, and for a render the locked parameter values the
/// token carries. Set only by <see cref="EmbedTokenAuthMiddleware"/>; the embed endpoints read it, never the body.
/// </summary>
internal sealed record EmbedRequestFeature(int GrantId, string GrantPublicId, int ProjectId, IReadOnlyDictionary<string, string> Parameters);

/// <summary>
/// Authenticates the anonymous embed routes (<see cref="BeaconEmbedPaths"/>), last in <c>UseBeaconAuthentication</c>, and
/// only while embedding is on. On an embed path it always replaces the caller, whatever an earlier middleware or the host's
/// default scheme put there: with the embed principal when the credential is valid, otherwise with an anonymous one. The
/// token exchange takes <c>Authorization: BeaconEmbedGrant &lt;secret&gt;</c>, the render <c>Authorization: BeaconEmbed
/// &lt;token&gt;</c> (never <c>Bearer</c>, so the API-key and bearer middlewares never see either) plus the grant's frame
/// marker (<see cref="FrameHeader"/>), and a render from another origin is refused. A refused credential is answered with
/// one generic 401 (the reason goes to the log and, when a credential was presented, <c>embed.token_refused</c>; never to the
/// caller), a store that cannot be read with 503. The limits of <see cref="EmbedRateLimiter"/> answer 429: a grant's budget
/// once its credential verified, the client's refusal budget only on a refusal. Never logs a token or a secret.
/// </summary>
public sealed class EmbedTokenAuthMiddleware(RequestDelegate next)
{
    public const string GrantScheme = "BeaconEmbedGrant";
    public const string TokenScheme = "BeaconEmbed";
    public const string FrameHeader = "X-Beacon-Embed-Frame";

    /// <summary>The body of every refusal on the embed routes.</summary>
    public const string UnavailableError = "embed_unavailable";

    public async Task InvokeAsync(HttpContext context)
    {
        var route = BeaconEmbedPaths.RouteOf(context);
        if (route == EmbedRoute.None)
        {
            await next(context);
            return;
        }

        // Nothing an earlier middleware or the host's default scheme authenticated stands on an embed path.
        SetAnonymous(context);
        context.Features.Set<EmbedRequestFeature?>(null);

        // The page itself is anonymous: its endpoint reads only whether the grant is live and its origins.
        if (route == EmbedRoute.Frame)
        {
            await next(context);
            return;
        }

        // The credential is checked before the client's refusal budget is looked at, so a request whose credential verifies
        // is never refused for other requests' refusals from the same address.
        var limiter = context.RequestServices.GetRequiredService<EmbedRateLimiter>();
        var logger = context.RequestServices.GetRequiredService<ILogger<EmbedTokenAuthMiddleware>>();
        var isRender = route == EmbedRoute.Render;
        var credential = CredentialOf(context.Request, isRender ? TokenScheme : GrantScheme);
        if (isRender && !IsSameOrigin(context))
        {
            await RefuseAsync(context, limiter, logger, EmbedRefusalCodes.Origin, null, presented: credential != null, StatusCodes.Status403Forbidden);
            return;
        }

        if (credential == null)
        {
            await RefuseAsync(context, limiter, logger, EmbedRefusalCodes.Unknown, null, presented: false, StatusCodes.Status401Unauthorized);
            return;
        }

        EmbedGrantResolution resolution;
        try
        {
            var resolver = context.RequestServices.GetRequiredService<EmbedGrantResolver>();
            resolution = isRender
                ? await resolver.ResolveTokenAsync(credential, context.RequestAborted)
                : await resolver.ResolveSecretAsync(credential, context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "An embed credential could not be checked: the grant store could not be read");
            await UnavailableAsync(context);
            return;
        }

        if (resolution.IsLive && isRender)
        {
            var codec = context.RequestServices.GetRequiredService<EmbedTokenCodec>();
            if (!codec.IsFrameMarkerOf(resolution.Grant!.PublicId, context.Request.Headers[FrameHeader].ToString()))
            {
                resolution = EmbedGrantResolution.Refused(EmbedRefusalCodes.Origin, resolution.GrantId);
            }
        }

        if (!resolution.IsLive)
        {
            await RefuseAsync(context, limiter, logger, resolution.Refusal ?? EmbedRefusalCodes.Unknown, resolution.GrantId, presented: true, StatusCodes.Status401Unauthorized);
            return;
        }

        var grant = resolution.Grant!;
        if (!limiter.TryAcquireGrant(exchange: !isRender, grant.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), out var retryAfter))
        {
            logger.LogWarning("Embed grant {GrantId} is over its per-minute {Route} limit", grant.Id, route);
            await TooManyAsync(context, retryAfter);
            return;
        }

        var principal = EmbedPrincipal.Create(grant.Id, isRender ? EmbedCredential.Token : EmbedCredential.GrantSecret);
        var now = (context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        var properties = new AuthenticationProperties
        {
            IssuedUtc = now,
            ExpiresUtc = resolution.Token?.ExpiresAt ?? now.AddMinutes(1)
        };
        BeaconAuthenticationFeatures.Set(context, AuthenticateResult.Success(new AuthenticationTicket(principal, properties, TokenScheme)));
        context.Features.Set<EmbedRequestFeature?>(new EmbedRequestFeature(
            grant.Id,
            grant.PublicId,
            grant.ProjectId,
            resolution.Token?.Parameters ?? new Dictionary<string, string>()));

        await next(context);
    }

    /// <summary>The one generic refusal body.</summary>
    internal static Task WriteUnavailableAsync(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.Headers.CacheControl = "no-store";

        return context.Response.WriteAsJsonAsync(new { error = UnavailableError }, context.RequestAborted);
    }

    internal static Task UnavailableAsync(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "5";

        return WriteUnavailableAsync(context, StatusCodes.Status503ServiceUnavailable);
    }

    // A render comes from Beacon's own embed page: a browser sends no Origin for it, or Beacon's own. Any other origin is a
    // page elsewhere calling the route, which the frame headers cannot stop.
    internal static bool IsSameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        var own = $"{context.Request.Scheme}://{context.Request.Host.Value}";
        if (string.Equals(origin, own, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var publicBaseUrl = context.RequestServices.GetService<IConfiguration>()?["Beacon:PublicBaseUrl"];

        return Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var baseUri)
            && string.Equals(origin, baseUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }

    private static string? CredentialOf(HttpRequest request, string scheme)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.Length <= scheme.Length + 1
            || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            || header[scheme.Length] != ' ')
        {
            return null;
        }

        var value = header[(scheme.Length + 1)..].Trim();

        return value.Length == 0 ? null : value;
    }

    private static void SetAnonymous(HttpContext context)
    {
        var anonymous = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        context.User = anonymous;
        context.Features.Set<IAuthenticateResultFeature?>(null);
    }

    private static async Task RefuseAsync(
        HttpContext context,
        EmbedRateLimiter limiter,
        ILogger logger,
        string reason,
        int? grantId,
        bool presented,
        int statusCode)
    {
        if (!limiter.TryAcquireRefusal(context, out var retryAfter))
        {
            await TooManyAsync(context, retryAfter);
            return;
        }

        logger.LogInformation("Embed request refused: {Reason} (grant {GrantId})", reason, grantId);

        // Only a credential that was presented is an event; at most so many a minute reach the trail.
        if (presented && limiter.TryAcquireRefusalAudit())
        {
            try
            {
                await context.RequestServices.GetRequiredService<ISecurityAuditRecorder>().RecordAsync(
                    new SecurityAuditEntry(SecurityAuditActions.EmbedTokenRefused)
                        .On(SecurityAuditResources.EmbedGrant, grantId)
                        .With(new SecurityAuditDetails()
                            .AddCode("reason", reason))
                        .Ending(SecurityAuditOutcomes.Refused),
                    context.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The refusal of an embed request could not be recorded in the security audit trail");
            }
        }

        await WriteUnavailableAsync(context, statusCode);
    }

    private static Task TooManyAsync(HttpContext context, TimeSpan retryAfter)
    {
        context.Response.Headers.RetryAfter = EmbedRateLimiter.RetryAfterSeconds(retryAfter);

        return WriteUnavailableAsync(context, StatusCodes.Status429TooManyRequests);
    }
}
