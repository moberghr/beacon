using System.Text.Json;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.SavedQueries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Beacon.Core.Services.Embed;

/// <summary>
/// Why an embed credential was refused, as <c>embed.token_refused</c> records it. Never returned to the caller, who gets the
/// same generic answer for each.
/// </summary>
internal static class EmbedRefusalCodes
{
    public const string Expired = "expired";
    public const string InvalidSignature = "invalid_signature";
    public const string Revoked = "revoked";
    public const string GrantExpired = "grant_expired";
    public const string Superseded = "superseded";
    public const string Unknown = "unknown";
    public const string Origin = "origin";
}

/// <summary>A grant as the embed routes act on it, read live from its row.</summary>
internal sealed record EmbedGrantState(
    int Id,
    string PublicId,
    int ProjectId,
    int QueryId,
    int QueryVersionId,
    string Name,
    IReadOnlyList<string> AllowedOrigins,
    IReadOnlyDictionary<string, string> FixedParameters,
    IReadOnlyList<string> LockedParameterNames,
    DateTime ExpiresTime,
    RunnableVersionInfo Version);

/// <summary>
/// The outcome of resolving an embed credential: the grant it names when it is live, otherwise the refusal code and the
/// grant's id when the credential named one.
/// </summary>
internal sealed record EmbedGrantResolution(EmbedGrantState? Grant, EmbedTokenPayload? Token, string? Refusal, int? GrantId)
{
    public bool IsLive => Grant != null && Refusal == null;

    public static EmbedGrantResolution Refused(string refusal, int? grantId = null) => new(null, null, refusal, grantId);
}

/// <summary>
/// Resolves an embed credential to its live grant: a grant secret (by its hash) for the token exchange, an embed token (its
/// signature, lifetime and audience, then the grant it names by public id) for the render, a public id for the frame page.
/// A grant is live while it is not revoked, not expired and its pinned version is still the query's runnable version
/// (<see cref="SavedQueryRunnableVersion"/>, strict mode included); a newer approved version supersedes it. The grant row is
/// read on every call, so a revoke stops new tokens and every outstanding one at once. Never logs a credential.
/// </summary>
internal sealed class EmbedGrantResolver(
    IDbContextFactory<BeaconContext> contextFactory,
    EmbedTokenCodec codec,
    BeaconConfiguration configuration,
    IOptions<EmbedOptions> options,
    TimeProvider clock)
{
    public async Task<EmbedGrantResolution> ResolveSecretAsync(string? secret, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || !EmbedGrantSecret.IsWellFormed(secret))
        {
            return EmbedGrantResolution.Refused(EmbedRefusalCodes.Unknown);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var grant = await EmbedGrantLookup.BySecretHash(context, EmbedGrantSecret.Hash(secret!))
            .FirstOrDefaultAsync(cancellationToken);

        return await LiveAsync(context, grant, null, cancellationToken);
    }

    public async Task<EmbedGrantResolution> ResolveTokenAsync(string? token, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return EmbedGrantResolution.Refused(EmbedRefusalCodes.Unknown);
        }

        var check = await codec.ValidateAsync(token);
        if (!check.IsValid)
        {
            return EmbedGrantResolution.Refused(check.Refusal switch
            {
                EmbedTokenRefusal.Expired => EmbedRefusalCodes.Expired,
                EmbedTokenRefusal.InvalidSignature or EmbedTokenRefusal.WrongAudience => EmbedRefusalCodes.InvalidSignature,
                _ => EmbedRefusalCodes.Unknown
            });
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var grant = await EmbedGrantLookup.ByPublicId(context, check.Payload!.GrantPublicId)
            .FirstOrDefaultAsync(cancellationToken);

        return await LiveAsync(context, grant, check.Payload, cancellationToken);
    }

    public async Task<EmbedGrantResolution> ResolvePublicIdAsync(string? publicId, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || !IsPublicId(publicId))
        {
            return EmbedGrantResolution.Refused(EmbedRefusalCodes.Unknown);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var grant = await EmbedGrantLookup.ByPublicId(context, publicId!)
            .FirstOrDefaultAsync(cancellationToken);

        return await LiveAsync(context, grant, null, cancellationToken);
    }

    /// <summary>Re-reads a grant by id (the one an embed caller acts for) and checks it is still live.</summary>
    public async Task<EmbedGrantResolution> ResolveIdAsync(int grantId, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return EmbedGrantResolution.Refused(EmbedRefusalCodes.Unknown, grantId);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var grant = await context.EmbedGrants
            .Where(x => x.Id == grantId)
            .FirstOrDefaultAsync(cancellationToken);

        return await LiveAsync(context, grant, null, cancellationToken);
    }

    /// <summary>A grant public id's form: 32 lowercase hex characters (checked before any lookup).</summary>
    public static bool IsPublicId(string? value)
    {
        return value is { Length: 32 } && value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private async Task<EmbedGrantResolution> LiveAsync(BeaconContext context, EmbedGrant? grant, EmbedTokenPayload? token, CancellationToken cancellationToken)
    {
        if (grant == null)
        {
            return EmbedGrantResolution.Refused(EmbedRefusalCodes.Unknown);
        }

        if (grant.RevokedTime != null)
        {
            return EmbedGrantResolution.Refused(EmbedRefusalCodes.Revoked, grant.Id);
        }

        if (grant.ExpiresTime <= clock.GetUtcNow().UtcDateTime)
        {
            return EmbedGrantResolution.Refused(EmbedRefusalCodes.GrantExpired, grant.Id);
        }

        var version = await SavedQueryRunnableVersion.ForQuery(context, grant.QueryId, SavedQueryRunnableVersion.RequiresApprovedHash(configuration))
            .Where(x => x.VersionId == grant.QueryVersionId)
            .FirstOrDefaultAsync(cancellationToken);
        if (version == null)
        {
            return EmbedGrantResolution.Refused(EmbedRefusalCodes.Superseded, grant.Id);
        }

        var state = StateOf(grant, version);
        if (state == null)
        {
            return EmbedGrantResolution.Refused(EmbedRefusalCodes.Unknown, grant.Id);
        }

        return new EmbedGrantResolution(state, token, null, grant.Id);
    }

    // The stored JSON as the create handler wrote it; a row that does not read is refused, never half-applied.
    private static EmbedGrantState? StateOf(EmbedGrant grant, RunnableVersionInfo version)
    {
        try
        {
            var origins = JsonSerializer.Deserialize<List<string>>(grant.AllowedOriginsJson) ?? [];
            var fixedParameters = JsonSerializer.Deserialize<Dictionary<string, string>>(grant.FixedParametersJson) ?? [];
            var locked = JsonSerializer.Deserialize<List<string>>(grant.LockedParameterNamesJson) ?? [];

            return new EmbedGrantState(
                grant.Id,
                grant.PublicId,
                grant.ProjectId,
                grant.QueryId,
                grant.QueryVersionId,
                grant.Name,
                origins,
                new Dictionary<string, string>(fixedParameters, StringComparer.Ordinal),
                locked,
                grant.ExpiresTime,
                version);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
