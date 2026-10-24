using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.SavedQueries;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services.Embed;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beacon.Core.Handlers.Embed;

/// <summary>
/// Exchanges a grant's secret for an embed token: the caller must be the grant itself (the embed middleware's principal for
/// a verified grant secret, never a user, a session, an API key or an embed token), and the grant must still be live
/// (<see cref="EmbedGrantResolver"/>: not revoked, not expired, its version not superseded). The body names a value for
/// exactly the grant's locked parameters, each of its parameter's type; the token carries them (readable by its viewer, so
/// never secrets). The token lives <c>Beacon:Embed:TokenTtlSeconds</c>, or the shorter time asked for, never beyond
/// <c>MaxTokenTtlSeconds</c> or the grant's own expiry. Records <c>embed.token_issued</c> with the grant's
/// <c>LastTokenIssuedTime</c> (ids and the lifetime only, never the token or a value).
/// </summary>
internal sealed class IssueEmbedTokenHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    EmbedGrantResolver grants,
    EmbedTokenCodec codec,
    IOptions<EmbedOptions> embedOptions,
    ISecurityAuditRecorder securityAudit,
    TimeProvider clock,
    ILogger<IssueEmbedTokenHandler> logger)
    : IRequestHandler<IssueEmbedTokenCommand, IssueEmbedTokenResult>
{
    public const string EmbedPathPrefix = "/beacon/embed/";

    public async Task<IssueEmbedTokenResult> Handle(IssueEmbedTokenCommand request, CancellationToken cancellationToken)
    {
        var options = embedOptions.Value;
        var caller = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (!options.Enabled
            || caller.Kind != BeaconPrincipalKind.Embed
            || caller.EmbedCredential != EmbedCredential.GrantSecret
            || caller.EmbedGrantId is not { } grantId
            || caller.BeaconUserId == null)
        {
            throw new EmbedUnavailableException(EmbedRefusalCodes.Unknown, caller.EmbedGrantId);
        }

        var resolution = await grants.ResolveIdAsync(grantId, cancellationToken);
        if (!resolution.IsLive)
        {
            throw new EmbedUnavailableException(resolution.Refusal ?? EmbedRefusalCodes.Unknown, grantId);
        }

        var grant = resolution.Grant!;
        var supplied = new Dictionary<string, string>(request.Parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var lockedError = LockedParameterError(grant, supplied);
        if (lockedError != null)
        {
            throw new EmbedRequestInvalidException(lockedError);
        }

        var now = clock.GetUtcNow();
        var ttl = TtlOf(request.TtlSeconds, options, grant.ExpiresTime, now);

        var issued = await codec.MintAsync(grant.PublicId, supplied, ttl);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.EmbedGrants
            .Where(x => x.Id == grant.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new EmbedUnavailableException(EmbedRefusalCodes.Unknown, grant.Id);
        row.LastTokenIssuedTime = now.UtcDateTime;

        var ttlSeconds = (int)ttl.TotalSeconds;
        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.EmbedTokenIssued)
                .On(SecurityAuditResources.EmbedGrant, grant.Id)
                .InProject(grant.ProjectId)
                .With(new SecurityAuditDetails()
                    .Add("ttlSeconds", ttlSeconds)),
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Embed token issued for grant {GrantId}, valid {TtlSeconds} s", grant.Id, ttlSeconds);

        return new IssueEmbedTokenResult(issued.Token, issued.ExpiresAt, EmbedPathPrefix + grant.PublicId + "#t=" + issued.Token);
    }

    // Exactly the locked parameters, each converting to its parameter's type (the fixed ones are the grant's own).
    private static string? LockedParameterError(EmbedGrantState grant, IReadOnlyDictionary<string, string> supplied)
    {
        foreach (var name in supplied.Keys)
        {
            if (!grant.LockedParameterNames.Contains(name, StringComparer.Ordinal))
            {
                return $"Parameter '{name}' is not one the host supplies for this grant.";
            }
        }

        var missing = grant.LockedParameterNames
            .Where(x => !supplied.ContainsKey(x))
            .ToList();
        if (missing.Count > 0)
        {
            return $"Missing value for parameter(s): {string.Join(", ", missing)}.";
        }

        var shape = SavedQueryToolRules.Inspect(grant.Version.StepsJson);
        foreach (var (name, value) in supplied)
        {
            var parameter = shape.Parameters
                .Where(x => x.Name == name)
                .FirstOrDefault();
            if (parameter == null)
            {
                return $"Parameter '{name}' is not one the host supplies for this grant.";
            }

            if (value == null)
            {
                return $"Missing value for parameter(s): {name}.";
            }

            var error = SavedQueryArgumentConverter.ConvertText(parameter, value, out _);
            if (error != null)
            {
                return error;
            }
        }

        return null;
    }

    private static TimeSpan TtlOf(int? requestedSeconds, EmbedOptions options, DateTime grantExpires, DateTimeOffset now)
    {
        if (requestedSeconds is < 1)
        {
            throw new EmbedRequestInvalidException($"An embed token lives between 1 and {options.MaxTokenTtlSeconds} seconds.");
        }

        var seconds = Math.Min(requestedSeconds ?? options.TokenTtlSeconds, options.MaxTokenTtlSeconds);
        var untilGrantExpires = (int)Math.Floor((grantExpires - now.UtcDateTime).TotalSeconds);

        return TimeSpan.FromSeconds(Math.Max(1, Math.Min(seconds, untilGrantExpires)));
    }
}

/// <summary>
/// Exchanges the grant secret the request presents (<c>Authorization: BeaconEmbedGrant &lt;secret&gt;</c>) for an embed
/// token. <see cref="Parameters"/> names a value for each of the grant's locked parameters; <see cref="TtlSeconds"/> may ask
/// for a shorter token than the default.
/// </summary>
[ProjectScopeExempt("the caller is an embed grant, not a user: the handler requires the grant's own principal, and every render re-checks the grant's project")]
public sealed record IssueEmbedTokenCommand(IReadOnlyDictionary<string, string>? Parameters, int? TtlSeconds) : IRequest<IssueEmbedTokenResult>;

/// <param name="EmbedPath">The embed page's path with the token in its fragment, which a browser never sends to a server.</param>
public sealed record IssueEmbedTokenResult(string Token, DateTimeOffset ExpiresAt, string EmbedPath);
